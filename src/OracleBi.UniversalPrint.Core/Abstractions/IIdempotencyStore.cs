using OracleBi.UniversalPrint.Models;

namespace OracleBi.UniversalPrint.Abstractions;

/// <summary>
/// Records "this submit has been claimed" so a redelivered submit message cannot create a
/// duplicate Universal Print job. Backed by Azure Blob Storage (one blob per idempotency key)
/// using an atomic create-if-not-exists as the claim primitive.
///
/// The claim is two-phase: <see cref="TryClaimAsync"/> reserves the key, then once the Universal
/// Print job is created the caller writes a commit marker with <see cref="CommitAsync"/>. A
/// redelivery uses <see cref="GetAsync"/> to tell whether the original attempt got far enough to
/// create the job (committed) or not (still in flight / crashed before committing).
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically claims <paramref name="idempotencyKey"/>. Returns <c>true</c> if the caller won
    /// the claim (and should proceed with the submit), or <c>false</c> if it was already claimed
    /// (the submit is a duplicate and should be skipped).
    /// </summary>
    Task<bool> TryClaimAsync(string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the commit marker for a claim once the Universal Print job has been created. After
    /// this returns, a redelivery can recover (re-drive tracking) instead of re-submitting.
    /// </summary>
    Task CommitAsync(string idempotencyKey, IdempotencyRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the record for a claim. Returns <c>null</c> when the key is not claimed, an
    /// uncommitted record (<see cref="IdempotencyRecord.IsCommitted"/> false) when claimed but the
    /// submit has not completed, or a committed record once the job exists.
    /// </summary>
    Task<IdempotencyRecord?> GetAsync(string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a previously made claim so the submit can be retried. Only safe to call when no
    /// Universal Print job was created (i.e. the failure happened before/at submit); releasing a
    /// committed claim would allow a duplicate print on retry.
    /// </summary>
    Task ReleaseAsync(string idempotencyKey, CancellationToken cancellationToken = default);
}
