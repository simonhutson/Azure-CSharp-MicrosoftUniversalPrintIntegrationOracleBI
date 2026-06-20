namespace OracleBi.UniversalPrint.Models;

/// <summary>
/// Tracks a single print job from Oracle BI rendering through Universal Print completion.
/// The <see cref="CorrelationId"/> flows through logs, queue messages, telemetry and any
/// dead-letter envelope so a DLQ event can always be tied back to its originating job.
/// </summary>
public sealed class PrintJob
{
    /// <summary>Stable id we assign when the job is created. Also used as the telemetry correlation id.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>The original render/print request.</summary>
    public required OracleBiReportRequest Request { get; init; }

    /// <summary>Universal Print printer id the document was submitted to.</summary>
    public string? PrinterId { get; set; }

    /// <summary>Universal Print job id returned by Graph once the job is created.</summary>
    public string? UniversalPrintJobId { get; set; }

    /// <summary>Current normalised lifecycle state.</summary>
    public PrintJobState State { get; set; } = PrintJobState.Pending;

    /// <summary>Last error detail captured, if any.</summary>
    public string? LastError { get; set; }

    /// <summary>How many times the job has been polled so far.</summary>
    public int PollAttempts { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
