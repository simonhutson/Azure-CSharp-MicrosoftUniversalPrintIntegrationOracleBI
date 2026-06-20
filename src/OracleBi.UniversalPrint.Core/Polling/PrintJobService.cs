using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OracleBi.UniversalPrint.Abstractions;
using OracleBi.UniversalPrint.Configuration;
using OracleBi.UniversalPrint.Models;

namespace OracleBi.UniversalPrint.Polling;

/// <summary>
/// Application entry point: submits an Oracle BI report to Universal Print and schedules the
/// first status poll on the queue. Callers (APIs, jobs, the submit Azure Function) use this.
/// </summary>
public sealed class PrintJobService
{
    private readonly IUniversalPrintProvider _provider;
    private readonly IPrintJobQueue _queue;
    private readonly PollingOptions _polling;
    private readonly PrintSecurityOptions _security;
    private readonly ILogger<PrintJobService> _logger;

    public PrintJobService(
        IUniversalPrintProvider provider,
        IPrintJobQueue queue,
        IOptions<PollingOptions> polling,
        IOptions<PrintSecurityOptions> security,
        ILogger<PrintJobService> logger)
    {
        _provider = provider;
        _queue = queue;
        _polling = polling.Value;
        _security = security.Value;
        _logger = logger;
    }

    /// <summary>Renders + submits the report, then enqueues the first poll message.</summary>
    public async Task<PrintJob> SubmitAndTrackAsync(
        OracleBiReportRequest request, CancellationToken cancellationToken = default)
    {
        // Defence in depth: only allow-listed report paths may be printed.
        if (!_security.IsReportPathAllowed(request.ReportPath))
        {
            _logger.LogWarning(
                "Rejected print request for disallowed report path {ReportPath}.", request.ReportPath);
            throw new ReportPathNotAllowedException(request.ReportPath);
        }

        var job = await _provider.SubmitAsync(request, cancellationToken);

        if (job.UniversalPrintJobId is null || job.PrinterId is null)
        {
            throw new InvalidOperationException("Submitted job is missing printer or Universal Print job id.");
        }

        var poll = new PollMessage
        {
            CorrelationId = job.CorrelationId,
            PrinterId = job.PrinterId,
            UniversalPrintJobId = job.UniversalPrintJobId,
            PollAttempts = 0,
            ScheduledFor = DateTimeOffset.UtcNow + _polling.InitialRepollDelay,
        };

        await _queue.EnqueuePollAsync(poll, _polling.InitialRepollDelay, cancellationToken);
        _logger.LogInformation(
            "Tracking print job {CorrelationId}; first poll scheduled in {Delay}.",
            job.CorrelationId, _polling.InitialRepollDelay);

        return job;
    }
}

/// <summary>Raised when a caller requests a report path that is not on the allow-list.</summary>
public sealed class ReportPathNotAllowedException(string reportPath)
    : Exception($"Report path '{reportPath}' is not permitted.")
{
    public string ReportPath { get; } = reportPath;
}
