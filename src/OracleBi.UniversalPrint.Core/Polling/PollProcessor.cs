using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OracleBi.UniversalPrint.Abstractions;
using OracleBi.UniversalPrint.Configuration;
using OracleBi.UniversalPrint.Models;
using OracleBi.UniversalPrint.Telemetry;

namespace OracleBi.UniversalPrint.Polling;

/// <summary>
/// Host-agnostic core of status polling. Given a poll message it fetches the Universal Print job
/// status and returns the action the host should take (complete / reschedule / dead-letter).
/// Shared by the in-process <see cref="PrintJobPollingWorker"/> and the Azure Functions queue
/// trigger so the retry / dead-letter rules live in exactly one place.
/// </summary>
public sealed partial class PollProcessor
{
    private readonly IUniversalPrintProvider _provider;
    private readonly PollingOptions _polling;
    private readonly QueueOptions _queue;
    private readonly PrintTelemetry _telemetry;
    private readonly ILogger<PollProcessor> _logger;

    public PollProcessor(
        IUniversalPrintProvider provider,
        IOptions<PollingOptions> polling,
        IOptions<QueueOptions> queue,
        PrintTelemetry telemetry,
        ILogger<PollProcessor> logger)
    {
        _provider = provider;
        _polling = polling.Value;
        _queue = queue.Value;
        _telemetry = telemetry;
        _logger = logger;
    }

    /// <summary>
    /// Evaluates a single poll message. <paramref name="deliveryAttempt"/> is the queue dequeue
    /// count (poison protection); <see cref="PollMessage.PollAttempts"/> is the logical poll count.
    /// </summary>
    public async Task<PollProcessingResult> ProcessAsync(
        PollMessage message, long deliveryAttempt, CancellationToken cancellationToken)
    {
        using var activity = _telemetry.StartActivity("print.poll", message.CorrelationId);
        activity?.SetTag("printer.id", message.PrinterId);
        activity?.SetTag("universalprint.job_id", message.UniversalPrintJobId);
        activity?.SetTag("poll.attempt", message.PollAttempts);
        activity?.SetTag("delivery.attempt", deliveryAttempt);

        // Poison protection: too many raw delivery attempts -> dead-letter.
        if (deliveryAttempt > _queue.MaxDeliveryAttempts)
        {
            LogMaxDeliveryExceeded(message.CorrelationId, _queue.MaxDeliveryAttempts);
            return PollProcessingResult.Dead(BuildEnvelope(
                message, DeadLetterReason.MaxDeliveryAttemptsExceeded, deliveryAttempt,
                exceptionType: null, detail: $"DequeueCount={deliveryAttempt}"));
        }

        var sw = Stopwatch.StartNew();
        PrintJobStatus status;
        try
        {
            status = await _provider.GetStatusAsync(
                message.PrinterId, message.UniversalPrintJobId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw; // shutdown / host cancellation — let the host retry the message later.
        }
        catch (Exception ex)
        {
            // The provider already retried transient errors. Re-throw so the host can abandon the
            // message (its dequeue count climbs and poison protection eventually dead-letters it).
            LogStatusPollThrew(ex, message.CorrelationId);
            throw;
        }
        finally
        {
            sw.Stop();
        }

        _telemetry.PollAttempted(message.PrinterId, sw.Elapsed.TotalMilliseconds, status.State);
        activity?.SetTag("result.state", status.State.ToString());

        switch (status.State)
        {
            case PrintJobState.Completed:
                _telemetry.JobCompleted(message.PrinterId, DateTimeOffset.UtcNow - message.ScheduledFor);
                LogJobCompleted(message.CorrelationId, message.UniversalPrintJobId);
                return PollProcessingResult.Done();

            case PrintJobState.Failed or PrintJobState.Abandoned:
                _telemetry.JobFailed(message.PrinterId, status.Description ?? "printJobFailed");
                LogJobFailed(message.CorrelationId, status.Description, string.Join(",", status.Details));
                return PollProcessingResult.Dead(BuildEnvelope(
                    message, DeadLetterReason.PrintJobFailed, deliveryAttempt,
                    exceptionType: null,
                    detail: status.Description ?? string.Join(",", status.Details)));

            default:
                // Still printing. Stop re-polling once the logical attempt budget is exhausted.
                var nextAttempt = message.PollAttempts + 1;
                if (nextAttempt >= _polling.MaxPollAttempts)
                {
                    LogPollTimeout(message.CorrelationId, status.RawProcessingState, nextAttempt);
                    return PollProcessingResult.Dead(BuildEnvelope(
                        message, DeadLetterReason.PollTimeoutExceeded, deliveryAttempt,
                        exceptionType: null,
                        detail: $"State={status.RawProcessingState} after {nextAttempt} polls"));
                }

                var delay = ComputeBackoff(nextAttempt);
                var next = new PollMessage
                {
                    CorrelationId = message.CorrelationId,
                    PrinterId = message.PrinterId,
                    UniversalPrintJobId = message.UniversalPrintJobId,
                    PollAttempts = nextAttempt,
                    ScheduledFor = DateTimeOffset.UtcNow + delay,
                };
                LogRePolling(message.CorrelationId, delay, nextAttempt);
                return PollProcessingResult.Later(next, delay);
        }
    }

    /// <summary>Exponential back-off (capped) for the next re-poll.</summary>
    private TimeSpan ComputeBackoff(int attempt)
    {
        var seconds = _polling.InitialRepollDelay.TotalSeconds * Math.Pow(2, attempt - 1);
        var capped = Math.Min(seconds, _polling.MaxRepollDelay.TotalSeconds);
        return TimeSpan.FromSeconds(capped);
    }

    private static DeadLetterEnvelope BuildEnvelope(
        PollMessage message, DeadLetterReason reason, long deliveryAttempt, string? exceptionType, string? detail) =>
        new()
        {
            CorrelationId = message.CorrelationId,
            Reason = reason,
            OriginalMessage = message,
            PrinterId = message.PrinterId,
            UniversalPrintJobId = message.UniversalPrintJobId,
            DeliveryAttempts = (int)deliveryAttempt,
            ExceptionType = exceptionType,
            ErrorDetail = detail,
        };

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Poll message for {CorrelationId} exceeded {Max} delivery attempts; dead-lettering.")]
    private partial void LogMaxDeliveryExceeded(string correlationId, int max);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Status poll threw for {CorrelationId}; will be retried via the queue.")]
    private partial void LogStatusPollThrew(Exception ex, string correlationId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Print job {CorrelationId} completed (UP job {JobId}).")]
    private partial void LogJobCompleted(string correlationId, string jobId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Print job {CorrelationId} failed: {Description} [{Details}]")]
    private partial void LogJobFailed(string correlationId, string? description, string details);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Print job {CorrelationId} still '{State}' after {Attempts} polls; dead-lettering.")]
    private partial void LogPollTimeout(string correlationId, string? state, int attempts);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Print job {CorrelationId} still printing; re-polling in {Delay} (attempt {Attempt}).")]
    private partial void LogRePolling(string correlationId, TimeSpan delay, int attempt);
}
