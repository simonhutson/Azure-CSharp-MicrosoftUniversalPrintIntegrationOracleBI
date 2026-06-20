using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OracleBi.UniversalPrint.Abstractions;
using OracleBi.UniversalPrint.Configuration;
using OracleBi.UniversalPrint.Models;
using OracleBi.UniversalPrint.Telemetry;

namespace OracleBi.UniversalPrint.Polling;

/// <summary>
/// Host-agnostic core of the asynchronous submit path. Given a submit message it claims the
/// idempotency key, renders + submits the Oracle BI report to Universal Print, and schedules the
/// first status poll. Returns the action the host should take (delete / dead-letter / retry).
/// Mirrors <see cref="PollProcessor"/> so the retry / dead-letter rules live in one place.
/// </summary>
public sealed class SubmitProcessor
{
    private readonly PrintJobService _printJobService;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly QueueOptions _queue;
    private readonly PrintTelemetry _telemetry;
    private readonly ILogger<SubmitProcessor> _logger;

    public SubmitProcessor(
        PrintJobService printJobService,
        IIdempotencyStore idempotencyStore,
        IOptions<QueueOptions> queue,
        PrintTelemetry telemetry,
        ILogger<SubmitProcessor> logger)
    {
        _printJobService = printJobService;
        _idempotencyStore = idempotencyStore;
        _queue = queue.Value;
        _telemetry = telemetry;
        _logger = logger;
    }

    /// <summary>
    /// Evaluates a single submit message. <paramref name="deliveryAttempt"/> is the queue dequeue
    /// count (poison protection).
    /// </summary>
    public async Task<SubmitProcessingResult> ProcessAsync(
        SubmitMessage message, long deliveryAttempt, CancellationToken cancellationToken)
    {
        using var activity = _telemetry.StartActivity("print.submit", message.CorrelationId);
        activity?.SetTag("delivery.attempt", deliveryAttempt);

        // Poison protection: too many raw delivery attempts -> dead-letter.
        if (deliveryAttempt > _queue.MaxDeliveryAttempts)
        {
            _logger.LogWarning(
                "Submit message for {CorrelationId} exceeded {Max} delivery attempts; dead-lettering.",
                message.CorrelationId, _queue.MaxDeliveryAttempts);
            return SubmitProcessingResult.Dead(BuildEnvelope(
                message, DeadLetterReason.MaxDeliveryAttemptsExceeded, deliveryAttempt,
                exceptionType: null, detail: $"DequeueCount={deliveryAttempt}"));
        }

        // Idempotency: the first delivery wins the claim. A redelivery (or a client retry with the
        // same key) does not win it and is handled by HandleDuplicateAsync so the Universal Print
        // job is created at most once.
        var claimed = await _idempotencyStore.TryClaimAsync(message.IdempotencyKey, cancellationToken);
        if (!claimed)
        {
            return await HandleDuplicateAsync(message, cancellationToken);
        }

        PrintJob job;
        try
        {
            // Irreversible step: this creates the Universal Print job. A failure here means no
            // durable job exists yet, so it is safe to release the claim and let the queue retry.
            job = await _printJobService.SubmitAsync(
                message.Request, message.CorrelationId, cancellationToken);
        }
        catch (ReportPathNotAllowedException ex)
        {
            // Permanent rejection, checked before any Graph call (no job was created). Keep the
            // claim so a retry stays a no-op, and dead-letter for visibility.
            _logger.LogWarning(
                "Submit for {CorrelationId} rejected: {Reason}", message.CorrelationId, ex.Message);
            return SubmitProcessingResult.Dead(BuildEnvelope(
                message, DeadLetterReason.SubmitRejected, deliveryAttempt,
                exceptionType: ex.GetType().Name, detail: ex.Message));
        }
        catch (Exception ex)
        {
            // Pre-submit failure: no Universal Print job exists, so release the claim and rethrow
            // so the host abandons the message and the next delivery can re-attempt the submit.
            _logger.LogWarning(ex,
                "Submit for {CorrelationId} failed before the job was created; releasing claim and retrying.",
                message.CorrelationId);
            await ReleaseClaimBestEffortAsync(message.IdempotencyKey);
            throw;
        }

        // The job now exists in Universal Print. From here we must NEVER release the claim or call
        // the provider again, or we would create a duplicate print. Record the commit marker first
        // (the durable "this succeeded" record a redelivery recovers from), then schedule tracking.
        // If either write fails it is retried by the queue; the committed marker lets the redelivery
        // re-drive the poll instead of re-submitting (see HandleDuplicateAsync).
        await _idempotencyStore.CommitAsync(
            message.IdempotencyKey,
            new IdempotencyRecord
            {
                CorrelationId = job.CorrelationId,
                UniversalPrintJobId = job.UniversalPrintJobId,
                PrinterId = job.PrinterId,
                CommittedAt = DateTimeOffset.UtcNow,
            },
            cancellationToken);

        await _printJobService.ScheduleFirstPollAsync(
            job.CorrelationId, job.PrinterId!, job.UniversalPrintJobId!, cancellationToken);

        return SubmitProcessingResult.Submitted(job);
    }

    /// <summary>
    /// Handles a submit whose idempotency key is already claimed. If the original attempt committed
    /// (the Universal Print job exists), tracking is re-driven so the job is polled even if the
    /// original attempt died before scheduling it — the provider is never called again. If the
    /// claim is uncommitted (a concurrent attempt is mid-submit, or a previous one crashed before
    /// creating/recording the job) the submit is deferred to a retry rather than silently dropped,
    /// so it either recovers once the in-flight attempt commits or dead-letters on max delivery.
    /// </summary>
    private async Task<SubmitProcessingResult> HandleDuplicateAsync(
        SubmitMessage message, CancellationToken cancellationToken)
    {
        var record = await _idempotencyStore.GetAsync(message.IdempotencyKey, cancellationToken);
        if (record is { IsCommitted: true })
        {
            _logger.LogInformation(
                "Submit for {CorrelationId} duplicates already-created job {JobId}; ensuring it is polled.",
                message.CorrelationId, record.UniversalPrintJobId);
            await _printJobService.ScheduleFirstPollAsync(
                record.CorrelationId ?? message.CorrelationId,
                record.PrinterId!,
                record.UniversalPrintJobId!,
                cancellationToken);
            return SubmitProcessingResult.Duplicate();
        }

        _logger.LogWarning(
            "Submit for {CorrelationId} found an uncommitted in-flight claim; deferring to retry.",
            message.CorrelationId);
        throw new SubmitClaimPendingException(message.CorrelationId);
    }

    private async Task ReleaseClaimBestEffortAsync(string idempotencyKey)
    {
        try
        {
            // Best-effort and intentionally not cancelled: releasing must still run during shutdown
            // so the submit can be retried rather than being silently skipped as a duplicate.
            await _idempotencyStore.ReleaseAsync(idempotencyKey, CancellationToken.None);
        }
        catch (Exception releaseEx)
        {
            _logger.LogError(releaseEx,
                "Failed to release idempotency claim {Key}; a retry may be skipped as a duplicate.",
                idempotencyKey);
        }
    }

    private static DeadLetterEnvelope BuildEnvelope(
        SubmitMessage message, DeadLetterReason reason, long deliveryAttempt, string? exceptionType, string? detail) =>
        new()
        {
            CorrelationId = message.CorrelationId,
            Reason = reason,
            PrinterId = message.Request.PrinterId,
            DeliveryAttempts = (int)deliveryAttempt,
            ExceptionType = exceptionType,
            ErrorDetail = detail,
        };
}

/// <summary>
/// Raised when a submit duplicates a claim that has not yet committed (a concurrent attempt is
/// mid-submit, or a previous one crashed before creating/recording the job). It signals the host
/// to retry the message later — once the in-flight attempt commits the redelivery recovers, and if
/// it never does the message dead-letters on the max-delivery threshold for inspection.
/// </summary>
public sealed class SubmitClaimPendingException(string correlationId)
    : Exception($"Submit for '{correlationId}' is waiting on an uncommitted idempotency claim.")
{
    public string CorrelationId { get; } = correlationId;
}
