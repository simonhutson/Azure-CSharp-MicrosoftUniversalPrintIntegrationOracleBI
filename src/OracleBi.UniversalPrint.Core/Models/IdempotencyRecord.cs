using System.Text.Json.Serialization;

namespace OracleBi.UniversalPrint.Models;

/// <summary>
/// The durable record stored under an idempotency claim. An empty claim (all fields null) means
/// "claimed but the submit has not completed yet"; a record carrying a
/// <see cref="UniversalPrintJobId"/> is the <em>commit marker</em> proving the Universal Print job
/// was actually created. Once committed, a redelivered submit must never call the provider again —
/// it can only re-drive tracking for the already-created job.
/// </summary>
public sealed class IdempotencyRecord
{
    /// <summary>Correlation id of the submit that won the claim.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Universal Print job id created for this claim. Non-null only once committed.</summary>
    public string? UniversalPrintJobId { get; init; }

    /// <summary>Universal Print printer id the job was submitted to.</summary>
    public string? PrinterId { get; init; }

    /// <summary>When the commit marker was written.</summary>
    public DateTimeOffset? CommittedAt { get; init; }

    /// <summary>True once the Universal Print job has been created and recorded.</summary>
    [JsonIgnore]
    public bool IsCommitted => UniversalPrintJobId is not null;
}
