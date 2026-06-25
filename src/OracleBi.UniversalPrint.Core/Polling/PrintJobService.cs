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
public sealed partial class PrintJobService
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

    /// <summary>Renders + submits the report under <paramref name="correlationId"/>, then enqueues the first poll message.</summary>
    public async Task<PrintJob> SubmitAndTrackAsync(
        OracleBiReportRequest request, string correlationId, CancellationToken cancellationToken = default)
    {
        var job = await SubmitAsync(request, correlationId, cancellationToken);
        await ScheduleFirstPollAsync(
            job.CorrelationId, job.PrinterId!, job.UniversalPrintJobId!, cancellationToken);
        return job;
    }

    /// <summary>
    /// Renders + submits the report to Universal Print under <paramref name="correlationId"/> and
    /// returns the tracked job. This is the <em>irreversible</em> step (it creates a Graph print
    /// job); callers must not retry it without idempotency protection. Tracking is scheduled
    /// separately via <see cref="ScheduleFirstPollAsync"/> so the two can be sequenced around a
    /// durable idempotency commit.
    /// </summary>
    public async Task<PrintJob> SubmitAsync(
        OracleBiReportRequest request, string correlationId, CancellationToken cancellationToken = default)
    {
        // Defence in depth: only allow-listed report paths may be printed.
        if (!_security.IsReportPathAllowed(request.ReportPath))
        {
            LogRejectedReportPath(request.ReportPath);
            throw new ReportPathNotAllowedException(request.ReportPath);
        }

        var job = await _provider.SubmitAsync(request, correlationId, cancellationToken);

        if (job.UniversalPrintJobId is null || job.PrinterId is null)
        {
            throw new InvalidOperationException("Submitted job is missing printer or Universal Print job id.");
        }

        return job;
    }

    /// <summary>
    /// Enqueues the first status poll for an already-created Universal Print job. Safe to call more
    /// than once for the same job (a duplicate poll is harmless), which is what lets a redelivered
    /// submit recover tracking without re-submitting.
    /// </summary>
    public async Task ScheduleFirstPollAsync(
        string correlationId, string printerId, string universalPrintJobId,
        CancellationToken cancellationToken = default)
    {
        var poll = new PollMessage
        {
            CorrelationId = correlationId,
            PrinterId = printerId,
            UniversalPrintJobId = universalPrintJobId,
            PollAttempts = 0,
            ScheduledFor = DateTimeOffset.UtcNow + _polling.InitialRepollDelay,
        };

        await _queue.EnqueuePollAsync(poll, _polling.InitialRepollDelay, cancellationToken);
        LogTrackingScheduled(correlationId, _polling.InitialRepollDelay);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Rejected print request for disallowed report path {ReportPath}.")]
    private partial void LogRejectedReportPath(string reportPath);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Tracking print job {CorrelationId}; first poll scheduled in {Delay}.")]
    private partial void LogTrackingScheduled(string correlationId, TimeSpan delay);
}

/// <summary>Raised when a caller requests a report path that is not on the allow-list.</summary>
public sealed class ReportPathNotAllowedException(string reportPath)
    : Exception($"Report path '{reportPath}' is not permitted.")
{
    public string ReportPath { get; } = reportPath;
}
