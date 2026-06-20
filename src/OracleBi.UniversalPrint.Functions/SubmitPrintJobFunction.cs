using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OracleBi.UniversalPrint.Abstractions;
using OracleBi.UniversalPrint.Configuration;
using OracleBi.UniversalPrint.Models;

namespace OracleBi.UniversalPrint.Functions;

/// <summary>
/// HTTP entry point. Validates the request, enforces the report-path allow-list, then enqueues a
/// submit message and returns 202 Accepted immediately — the heavy render + Universal Print upload
/// runs off-thread on the submit queue (see <see cref="RenderAndSubmitFunction"/>). Returns the
/// correlation id (for end-to-end tracking) and the idempotency key used to de-duplicate retries.
/// </summary>
public sealed class SubmitPrintJobFunction
{
    private const string IdempotencyHeader = "Idempotency-Key";

    private readonly IPrintJobQueue _queue;
    private readonly PrintSecurityOptions _security;
    private readonly ILogger<SubmitPrintJobFunction> _logger;

    public SubmitPrintJobFunction(
        IPrintJobQueue queue,
        IOptions<PrintSecurityOptions> security,
        ILogger<SubmitPrintJobFunction> logger)
    {
        _queue = queue;
        _security = security.Value;
        _logger = logger;
    }

    [Function(nameof(SubmitPrintJobFunction))]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "print-jobs")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        OracleBiReportRequest? reportRequest;
        try
        {
            reportRequest = await request.ReadFromJsonAsync<OracleBiReportRequest>(cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return await Text(request, HttpStatusCode.BadRequest, "Request body is not a valid print job.", cancellationToken);
        }

        if (reportRequest is null || string.IsNullOrWhiteSpace(reportRequest.ReportPath))
        {
            return await Text(request, HttpStatusCode.BadRequest, "A reportPath is required.", cancellationToken);
        }

        // Allow-list check up front so disallowed paths are rejected synchronously with 403 rather
        // than being accepted (202) and only failing later on the queue.
        if (!_security.IsReportPathAllowed(reportRequest.ReportPath))
        {
            _logger.LogWarning(
                "Rejected print request for disallowed report path {ReportPath}.", reportRequest.ReportPath);
            return await Text(request, HttpStatusCode.Forbidden, "The requested report path is not permitted.", cancellationToken);
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var idempotencyKey = ReadIdempotencyKey(request) ?? Guid.NewGuid().ToString("N");

        await _queue.EnqueueSubmitAsync(new SubmitMessage
        {
            CorrelationId = correlationId,
            IdempotencyKey = idempotencyKey,
            Request = reportRequest,
        }, cancellationToken);

        _logger.LogInformation(
            "Accepted print job {CorrelationId} (idempotency {IdempotencyKey}); queued for submit.",
            correlationId, idempotencyKey);

        var response = request.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new
        {
            correlationId,
            idempotencyKey,
            state = "Accepted",
        }, cancellationToken);
        return response;
    }

    private static string? ReadIdempotencyKey(HttpRequestData request)
    {
        if (request.Headers.TryGetValues(IdempotencyHeader, out var values))
        {
            var key = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(key))
            {
                return key.Trim();
            }
        }

        return null;
    }

    private static async Task<HttpResponseData> Text(
        HttpRequestData request, HttpStatusCode status, string message, CancellationToken cancellationToken)
    {
        var response = request.CreateResponse(status);
        await response.WriteStringAsync(message, cancellationToken);
        return response;
    }
}
