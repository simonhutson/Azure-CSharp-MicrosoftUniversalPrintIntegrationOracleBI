using OracleBi.UniversalPrint.Models;

namespace OracleBi.UniversalPrint.Abstractions;

/// <summary>
/// Writes failed messages to the dead-letter queue, emitting telemetry so the events can be
/// alerted on and correlated back to the originating print job.
/// </summary>
public interface IDeadLetterQueue
{
    Task DeadLetterAsync(DeadLetterEnvelope envelope, CancellationToken cancellationToken = default);
}
