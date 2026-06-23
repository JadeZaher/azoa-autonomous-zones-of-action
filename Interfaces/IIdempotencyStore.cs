using AZOA.WebAPI.Models.Idempotency;

namespace AZOA.WebAPI.Interfaces;

/// <summary>
/// Result of an atomic claim-or-get against the idempotency ledger.
/// </summary>
/// <param name="Won">
/// <c>true</c> if THIS caller inserted the row and therefore owns the right to
/// execute the irreversible effect exactly once. <c>false</c> if a record
/// already existed (a duplicate/concurrent request); inspect
/// <see cref="Record"/> to replay the prior outcome.
/// </param>
/// <param name="Record">
/// The authoritative record. When <see cref="Won"/> is <c>true</c> this is the
/// freshly-inserted <see cref="IdempotencyState.InProgress"/> row. When
/// <c>false</c> it is the pre-existing record (which may be InProgress,
/// Completed, or Failed).
/// </param>
public sealed record IdempotencyClaim(bool Won, IdempotencyRecord Record);

/// <summary>
/// Exactly-once execution ledger for irreversible operations.
///
/// Usage contract:
/// 1. Call <see cref="TryClaimAsync"/> with the idempotency key BEFORE any
///    irreversible (on-chain) effect.
/// 2. If <see cref="IdempotencyClaim.Won"/> is <c>true</c>, perform the effect,
///    then call <see cref="CompleteAsync"/> (success) or <see cref="FailAsync"/>
///    (failure).
/// 3. If <see cref="IdempotencyClaim.Won"/> is <c>false</c>, do NOT perform the
///    effect. Inspect <see cref="IdempotencyClaim.Record"/> and replay its
///    cached result/error (or surface "in progress" for an InProgress record).
///
/// Atomicity is provided by the database UNIQUE constraint on the key; the
/// implementation translates a unique-violation on insert into
/// <see cref="IdempotencyClaim.Won"/> == <c>false</c> + the existing record.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically claim the key or return the existing record.
    /// Inserts a new <see cref="IdempotencyState.InProgress"/> row when the key
    /// is unseen (returns <c>Won=true</c>); on a unique-constraint violation
    /// (concurrent/duplicate request) re-reads and returns <c>Won=false</c>
    /// with the existing record.
    /// </summary>
    Task<IdempotencyClaim> TryClaimAsync(string key, string operationType, CancellationToken ct);

    /// <summary>
    /// Mark a claimed key as <see cref="IdempotencyState.Completed"/> and cache
    /// the serialized result for replay to duplicate callers.
    /// </summary>
    Task CompleteAsync(string key, string resultPayload, CancellationToken ct);

    /// <summary>
    /// Mark a claimed key as <see cref="IdempotencyState.Failed"/> with the
    /// given error. (Whether a failed key may be retried is a policy decision
    /// for the caller; this store only records terminal failure.)
    /// </summary>
    Task FailAsync(string key, string error, CancellationToken ct);

    /// <summary>
    /// Fetch the record for a key, or <c>null</c> if the key has never been claimed.
    /// </summary>
    Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct);
}
