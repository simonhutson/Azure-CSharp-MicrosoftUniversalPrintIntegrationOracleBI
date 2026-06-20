using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using OracleBi.UniversalPrint.Models;
using OracleBi.UniversalPrint.Polling;

namespace OracleBi.UniversalPrint.Functions;

/// <summary>
/// HTTP entry point that renders an Oracle BI report, submits it to Universal Print, and starts
/// status tracking by enqueuing the first poll message. Returns the correlation id the caller can
/// use to track the job end-to-end (it flows through telemetry and any DLQ event).
/// </summary>
public sealed class SubmitPrintJobFunction
{
    private readonly PrintJobService _printJobService;
    private readonly ILogger<SubmitPrintJobFunction> _logger;

    public SubmitPrintJobFunction(PrintJobService printJobService, ILogger<SubmitPrintJobFunction> logger)
    {
        _printJobService = printJobService;
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
            var malformed = request.CreateResponse(HttpStatusCode.BadRequest);
            await malformed.WriteStringAsync("Request body is not a valid print job.", cancellationToken);
            return malformed;
        }

        if (reportRequest is null || string.IsNullOrWhiteSpace(reportRequest.ReportPath))
        {
            var bad = request.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("A reportPath is required.", cancellationToken);
            return bad;
        }

        PrintJob job;
        try
        {
            job = await _printJobService.SubmitAndTrackAsync(reportRequest, cancellationToken);
        }
        catch (ReportPathNotAllowedException)
        {
            var forbidden = request.CreateResponse(HttpStatusCode.Forbidden);
            await forbidden.WriteStringAsync("The requested report path is not permitted.", cancellationToken);
            return forbidden;
        }

        _logger.LogInformation("Accepted print job {CorrelationId}.", job.CorrelationId);

        var response = request.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new
        {
            correlationId = job.CorrelationId,
            universalPrintJobId = job.UniversalPrintJobId,
            printerId = job.PrinterId,
            state = job.State.ToString(),
        }, cancellationToken);
        return response;
    }
}
