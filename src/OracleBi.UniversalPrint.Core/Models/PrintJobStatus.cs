namespace OracleBi.UniversalPrint.Models;

/// <summary>
/// Snapshot of a Universal Print job's status as returned by Microsoft Graph.
/// </summary>
public sealed class PrintJobStatus
{
    public required PrintJobState State { get; init; }

    /// <summary>Raw Universal Print processing state, e.g. "pending", "processing", "completed".</summary>
    public string? RawProcessingState { get; init; }

    /// <summary>Raw detail codes from Graph, e.g. "completedSuccessfully", "documentUpdated".</summary>
    public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();

    /// <summary>Human-readable description, useful for logs and the DLQ envelope.</summary>
    public string? Description { get; init; }

    /// <summary>True if Graph reports the job has reached a terminal state.</summary>
    public bool IsTerminal => State.IsTerminal();
}

/// <summary>
/// The message body placed on the poll queue. One message == "poll this job again".
/// </summary>
public sealed class PollMessage
{
    public required string CorrelationId { get; init; }

    public required string PrinterId { get; init; }

    public required string UniversalPrintJobId { get; init; }

    /// <summary>Number of poll attempts already performed for this job.</summary>
    public int PollAttempts { get; init; }

    /// <summary>When this poll message was scheduled to become visible.</summary>
    public DateTimeOffset ScheduledFor { get; init; } = DateTimeOffset.UtcNow;
}
