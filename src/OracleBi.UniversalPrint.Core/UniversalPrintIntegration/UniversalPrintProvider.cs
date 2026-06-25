using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OracleBi.UniversalPrint.Abstractions;
using OracleBi.UniversalPrint.Configuration;
using OracleBi.UniversalPrint.Models;
using OracleBi.UniversalPrint.Resilience;
using OracleBi.UniversalPrint.Telemetry;
using Polly;

namespace OracleBi.UniversalPrint.UniversalPrintIntegration;

/// <summary>
/// Custom Universal Print provider for Oracle BI. Renders a BI Publisher report and submits it
/// to a Universal Print printer using the Microsoft Graph print APIs:
///   1. create print job
///   2. create an upload session for the job's document
///   3. upload the document bytes in chunks
///   4. start the job
///   5. poll job status
/// </summary>
public sealed partial class UniversalPrintProvider : IUniversalPrintProvider
{
    // Graph upload sessions require chunks that are a multiple of 320 KiB.
    private const int UploadChunkSize = 5 * 320 * 1024; // 1.6 MB

    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    private readonly HttpClient _httpClient;
    private readonly IOracleBiClient _oracleBiClient;
    private readonly UniversalPrintOptions _options;
    private readonly ILogger<UniversalPrintProvider> _logger;
    private readonly PrintTelemetry _telemetry;
    private readonly TokenCredential _credential;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    public UniversalPrintProvider(
        HttpClient httpClient,
        IOracleBiClient oracleBiClient,
        IOptions<UniversalPrintOptions> options,
        ILogger<UniversalPrintProvider> logger,
        PrintTelemetry telemetry)
    {
        _httpClient = httpClient;
        _oracleBiClient = oracleBiClient;
        _options = options.Value;
        _logger = logger;
        _telemetry = telemetry;
        _pipeline = ResiliencePipelines.CreateHttpPipeline(logger);
        _credential = CreateCredential(_options);

        _httpClient.BaseAddress ??= new Uri(_options.GraphBaseUrl.TrimEnd('/') + "/");
    }

    private static TokenCredential CreateCredential(UniversalPrintOptions options)
    {
        if (options.UseManagedIdentity || string.IsNullOrEmpty(options.ClientSecret))
        {
            return new DefaultAzureCredential();
        }

        return new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret);
    }

    public async Task<PrintJob> SubmitAsync(
        OracleBiReportRequest request,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var document = await _oracleBiClient.RenderReportAsync(request, cancellationToken);
        return await SubmitDocumentAsync(request, document, correlationId, cancellationToken);
    }

    public async Task<PrintJob> SubmitDocumentAsync(
        OracleBiReportRequest request,
        OracleBiDocument document,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var printerId = request.PrinterId ?? _options.DefaultPrinterId;

        using var activity = _telemetry.StartActivity("universalprint.submit", correlationId);
        activity?.SetTag("printer.id", printerId);

        var job = new PrintJob
        {
            CorrelationId = correlationId,
            Request = request,
            PrinterId = printerId,
            State = PrintJobState.Printing,
        };

        try
        {
            var jobId = await CreateJobAsync(printerId, cancellationToken);
            var documentId = await GetFirstDocumentIdAsync(printerId, jobId, cancellationToken);
            var uploadUrl = await CreateUploadSessionAsync(
                printerId, jobId, documentId, document, cancellationToken);
            await UploadDocumentAsync(uploadUrl, document, cancellationToken);
            await StartJobAsync(printerId, jobId, cancellationToken);

            job.UniversalPrintJobId = jobId;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            _telemetry.JobSubmitted(printerId);
            LogJobSubmitted(jobId, correlationId, printerId);

            return job;
        }
        catch (Exception ex)
        {
            job.State = PrintJobState.Failed;
            job.LastError = ex.Message;
            _telemetry.JobFailed(printerId, ex.GetType().Name);
            LogJobSubmitFailed(ex, correlationId);
            throw;
        }
    }

    public async Task<PrintJobStatus> GetStatusAsync(
        string printerId,
        string universalPrintJobId,
        CancellationToken cancellationToken = default)
    {
        var uri = $"print/printers/{printerId}/jobs/{universalPrintJobId}?$select=id,status";
        using var response = await SendAsync(HttpMethod.Get, uri, contentFactory: null, cancellationToken);
        await EnsureSuccessAsync(response, "get job status", cancellationToken);

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var status = doc.RootElement.GetProperty("status");
        var processingState = status.TryGetProperty("state", out var s) ? s.GetString() : null;
        var description = status.TryGetProperty("description", out var d) ? d.GetString() : null;

        var details = new List<string>();
        if (status.TryGetProperty("details", out var detailArray) &&
            detailArray.ValueKind == JsonValueKind.Array)
        {
            details.AddRange(detailArray.EnumerateArray()
                .Select(e => e.GetString())
                .Where(v => v is not null)!
                .Cast<string>());
        }

        return new PrintJobStatus
        {
            State = UniversalPrintStatusMapper.MapState(processingState, details),
            RawProcessingState = processingState,
            Description = description,
            Details = details,
        };
    }

    private async Task<string> CreateJobAsync(string printerId, CancellationToken ct)
    {
        var body = new
        {
            configuration = new { feedOrientation = "longEdgeFirst", quality = "medium" },
        };
        using var response = await SendAsync(
            HttpMethod.Post, $"print/printers/{printerId}/jobs", () => JsonContent.Create(body), ct);
        await EnsureSuccessAsync(response, "create print job", ct);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.GetProperty("id").GetString()
               ?? throw new UniversalPrintException("Create job response did not contain an id.");
    }

    private async Task<string> GetFirstDocumentIdAsync(string printerId, string jobId, CancellationToken ct)
    {
        using var response = await SendAsync(
            HttpMethod.Get, $"print/printers/{printerId}/jobs/{jobId}/documents", contentFactory: null, ct);
        await EnsureSuccessAsync(response, "list job documents", ct);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var value = doc.RootElement.GetProperty("value");
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
        {
            throw new UniversalPrintException("Print job was created without a document.");
        }

        return value[0].GetProperty("id").GetString()
               ?? throw new UniversalPrintException("Document entry did not contain an id.");
    }

    private async Task<string> CreateUploadSessionAsync(
        string printerId, string jobId, string documentId, OracleBiDocument document, CancellationToken ct)
    {
        var body = new
        {
            properties = new
            {
                documentName = document.FileName,
                contentType = document.ContentType,
                size = document.Length,
            },
        };
        using var response = await SendAsync(
            HttpMethod.Post,
            $"print/printers/{printerId}/jobs/{jobId}/documents/{documentId}/createUploadSession",
            () => JsonContent.Create(body), ct);
        await EnsureSuccessAsync(response, "create upload session", ct);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.GetProperty("uploadUrl").GetString()
               ?? throw new UniversalPrintException("Upload session did not return an uploadUrl.");
    }

    private async Task UploadDocumentAsync(string uploadUrl, OracleBiDocument document, CancellationToken ct)
    {
        var total = document.Content.LongLength;
        long offset = 0;

        // The uploadUrl is pre-authorised; do NOT attach the Graph bearer token to these PUTs.
        while (offset < total)
        {
            var chunkSize = (int)Math.Min(UploadChunkSize, total - offset);
            var chunk = new ReadOnlyMemory<byte>(document.Content, (int)offset, chunkSize);
            var rangeStart = offset;
            var rangeEnd = offset + chunkSize - 1;

            using var response = await _pipeline.ExecuteAsync(
                async token =>
                {
                    // Rebuild the request + content each attempt so retries are safe.
                    using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
                    {
                        Content = new ReadOnlyMemoryContent(chunk),
                    };
                    request.Content.Headers.ContentLength = chunkSize;
                    request.Content.Headers.ContentRange =
                        new ContentRangeHeaderValue(rangeStart, rangeEnd, total);
                    return await _httpClient.SendAsync(request, token);
                },
                ct);

            await EnsureSuccessAsync(response, $"upload chunk {offset}-{rangeEnd}", ct);
            offset += chunkSize;
        }
    }

    private async Task StartJobAsync(string printerId, string jobId, CancellationToken ct)
    {
        using var response = await SendAsync(
            HttpMethod.Post, $"print/printers/{printerId}/jobs/{jobId}/start", contentFactory: null, ct);
        await EnsureSuccessAsync(response, "start print job", ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string uri, Func<HttpContent>? contentFactory, CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(GraphScopes), ct);

        return await _pipeline.ExecuteAsync(
            async innerCt =>
            {
                // Build the request (and its content) fresh on every attempt so retries are safe.
                using var message = new HttpRequestMessage(method, uri)
                {
                    Content = contentFactory?.Invoke(),
                };
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                return await _httpClient.SendAsync(message, innerCt);
            },
            ct);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // Keep the Graph response body out of the exception message — it flows into the dead-letter
        // envelope and logs. Capture it only at Debug level for diagnostics.
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            LogOperationFailedDetail(operation, (int)response.StatusCode, body.Length <= 512 ? body : body[..512]);
        }

        throw new UniversalPrintException(
            $"Universal Print '{operation}' failed with status {(int)response.StatusCode}.");
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Submitted Universal Print job {JobId} (correlation {CorrelationId}) to printer {PrinterId}.")]
    private partial void LogJobSubmitted(string jobId, string correlationId, string printerId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Failed to submit Universal Print job (correlation {CorrelationId}).")]
    private partial void LogJobSubmitFailed(Exception ex, string correlationId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Universal Print '{Operation}' failed: {Status} {Body}")]
    private partial void LogOperationFailedDetail(string operation, int status, string body);
}

/// <summary>Raised for non-transient Universal Print / Microsoft Graph failures (after retries).</summary>
public sealed class UniversalPrintException(string message) : Exception(message);
