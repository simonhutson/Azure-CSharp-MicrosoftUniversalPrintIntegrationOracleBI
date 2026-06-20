namespace OracleBi.UniversalPrint.Models;

/// <summary>
/// Why a message ended up on the dead-letter queue. Used for routing, dashboards and alerts.
/// </summary>
public enum DeadLetterReason
{
    /// <summary>Exceeded the maximum number of delivery / processing attempts.</summary>
    MaxDeliveryAttemptsExceeded,

    /// <summary>The poll message body could not be deserialized (poison message).</summary>
    DeserializationFailure,

    /// <summary>Universal Print reported the job failed permanently.</summary>
    PrintJobFailed,

    /// <summary>Job stayed non-terminal beyond the maximum poll attempts.</summary>
    PollTimeoutExceeded,

    /// <summary>An unexpected, non-transient error occurred.</summary>
    UnhandledError,
}

/// <summary>
/// Envelope written to the dead-letter queue. It carries enough context to correlate the DLQ
/// event back to the original print job and to drive alerting / dashboards / replay.
/// </summary>
public sealed class DeadLetterEnvelope
{
    /// <summary>Correlation id of the originating print job — the key for DLQ ↔ job correlation.</summary>
    public required string CorrelationId { get; init; }

    public required DeadLetterReason Reason { get; init; }

    /// <summary>Short machine-readable reason code mirrored into queue message metadata for filtering.</summary>
    public string ReasonCode => Reason.ToString();

    /// <summary>The original poll message that failed, when available.</summary>
    public PollMessage? OriginalMessage { get; init; }

    public string? PrinterId { get; init; }

    public string? UniversalPrintJobId { get; init; }

    /// <summary>How many times delivery was attempted before dead-lettering.</summary>
    public int DeliveryAttempts { get; init; }

    /// <summary>Exception type name, if the failure came from an exception.</summary>
    public string? ExceptionType { get; init; }

    public string? ErrorDetail { get; init; }

    public DateTimeOffset DeadLetteredAt { get; init; } = DateTimeOffset.UtcNow;
}
