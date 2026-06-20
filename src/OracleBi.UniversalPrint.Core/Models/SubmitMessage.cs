namespace OracleBi.UniversalPrint.Models;

/// <summary>
/// The message body placed on the submit queue. One message == "render this Oracle BI report and
/// submit it to Universal Print". The heavy render + Graph upload work runs off the HTTP request
/// thread so the API can return 202 immediately.
/// </summary>
public sealed class SubmitMessage
{
    /// <summary>Correlation id assigned at intake; flows through logs, telemetry, the job and any DLQ event.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Idempotency key for the submit. A redelivered message (or a client retry carrying the same
    /// key) claims the same blob, so the Universal Print job is created at most once per key.
    /// </summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>The original render/print request to fulfil.</summary>
    public required OracleBiReportRequest Request { get; init; }
}
