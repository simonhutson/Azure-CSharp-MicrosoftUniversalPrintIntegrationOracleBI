using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OracleBi.UniversalPrint.Abstractions;
using OracleBi.UniversalPrint.Configuration;
using OracleBi.UniversalPrint.Models;
using OracleBi.UniversalPrint.Resilience;
using OracleBi.UniversalPrint.Telemetry;
using Polly;

namespace OracleBi.UniversalPrint.OracleBiIntegration;

/// <summary>
/// Calls the Oracle BI Publisher REST API (v1 "reports/{path}/run") to render a report and
/// returns its bytes. Wrapped with the shared retry pipeline + tracing.
/// </summary>
public sealed class OracleBiClient : IOracleBiClient
{
    private readonly HttpClient _httpClient;
    private readonly OracleBiOptions _options;
    private readonly ILogger<OracleBiClient> _logger;
    private readonly PrintTelemetry _telemetry;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    public OracleBiClient(
        HttpClient httpClient,
        IOptions<OracleBiOptions> options,
        ILogger<OracleBiClient> logger,
        PrintTelemetry telemetry)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _telemetry = telemetry;
        _pipeline = ResiliencePipelines.CreateHttpPipeline(logger);

        var baseUri = new Uri(_options.BaseUrl.TrimEnd('/') + "/");

        // Basic auth transmits the credentials with every request, so refuse plaintext transport.
        if (!_options.AllowInsecureTransport &&
            !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "OracleBi:BaseUrl must use HTTPS. Set OracleBi:AllowInsecureTransport=true only for local testing.");
        }

        _httpClient.BaseAddress ??= baseUri;
        _httpClient.Timeout = _options.RequestTimeout;

        var basic = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
    }

    public async Task<OracleBiDocument> RenderReportAsync(
        OracleBiReportRequest request,
        CancellationToken cancellationToken = default)
    {
        using var activity = _telemetry.StartActivity("oraclebi.render", request.ReportPath);
        activity?.SetTag("oraclebi.report_path", request.ReportPath);
        activity?.SetTag("oraclebi.output_format", request.OutputFormat);

        // BI Publisher REST: POST {base}/services/rest/v1/reports/{encodedPath}/run
        var encodedPath = Uri.EscapeDataString(request.ReportPath.TrimStart('/'));
        var requestUri = $"services/rest/v1/reports/{encodedPath}/run";

        var payload = new
        {
            attributeFormat = request.OutputFormat,
            attributeLocale = "en-US",
            parameterNameValues = new
            {
                listOfParamNameValues = request.Parameters
                    .Select(p => new { item = new { name = p.Key, values = new { item = new[] { p.Value } } } })
                    .ToArray(),
            },
        };

        var response = await _pipeline.ExecuteAsync(
            async ct =>
            {
                using var content = JsonContent.Create(payload);
                using var message = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = content };
                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                return await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Do not log the response body at Error level: it may contain report data / PII.
            _logger.LogError(
                "Oracle BI render failed for {ReportPath}. Status={Status}",
                request.ReportPath, (int)response.StatusCode);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var body = await SafeReadAsync(response, cancellationToken);
                _logger.LogDebug(
                    "Oracle BI failure detail for {ReportPath}: {Body}",
                    request.ReportPath, Truncate(body, 512));
            }
            throw new OracleBiRenderException(
                $"Oracle BI render failed with status {(int)response.StatusCode} for '{request.ReportPath}'.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/pdf";
        var fileName = $"{SanitizeFileName(request.DocumentName)}.{MapExtension(request.OutputFormat)}";

        activity?.SetTag("oraclebi.bytes", bytes.LongLength);
        _logger.LogInformation(
            "Rendered Oracle BI report {ReportPath} ({Bytes} bytes, {ContentType}).",
            request.ReportPath, bytes.LongLength, contentType);

        return new OracleBiDocument { Content = bytes, ContentType = contentType, FileName = fileName };
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            return $"<unreadable body: {ex.Message}>";
        }
    }

    private static string MapExtension(string format) => format.ToLowerInvariant() switch
    {
        "pdf" => "pdf",
        "html" => "html",
        "rtf" => "rtf",
        "excel" or "xlsx" => "xlsx",
        _ => "bin",
    };

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

/// <summary>Raised when Oracle BI Publisher cannot render a report (after retries).</summary>
public sealed class OracleBiRenderException(string message) : Exception(message);
