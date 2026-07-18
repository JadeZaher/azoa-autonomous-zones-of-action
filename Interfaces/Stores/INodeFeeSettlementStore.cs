using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

public interface INodeFeeSettlementStore
{
    /// <summary>
    /// Atomically creates the non-terminal parent idempotency claim and immutable settlement, or
    /// returns their existing paired records. A partial historical pair is rejected rather than
    /// repaired implicitly.
    /// </summary>
    Task<AZOAResult<NodeFeeSettlementAdmissionResult>> AdmitAsync(
        NodeFeeSettlement settlement,
        string parentIdempotencyKey,
        CancellationToken ct = default);

    /// <summary>Loads a settlement by its deterministic record id, or null when absent.</summary>
    Task<AZOAResult<NodeFeeSettlement?>> GetAsync(string settlementId, CancellationToken ct = default);

    /// <summary>
    /// Loads the sole immutable accepted atomic-group receipt deterministically bound to
    /// <paramref name="settlementId"/>. A missing receipt is not an error; a receipt
    /// whose record link does not match that settlement is a durable integrity failure.
    /// </summary>
    Task<AZOAResult<NodeFeeAtomicGroup?>> GetAcceptedAtomicGroupAsync(
        string settlementId,
        CancellationToken ct = default);

    /// <summary>
    /// Lists at most <paramref name="batchSize"/> non-terminal settlements that are due for recovery
    /// or whose lease has expired. The returned rows are candidates only; callers must claim each row.
    /// </summary>
    Task<AZOAResult<IReadOnlyList<NodeFeeSettlement>>> ListRecoverableAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically claims a candidate when its version and due-or-expired-lease predicate still match.
    /// A null result is expected contention; a non-null result carries the exact lease token and version
    /// required by a later guarded transition.
    /// </summary>
    Task<AZOAResult<NodeFeeSettlement?>> TryClaimRecoveryAsync(
        NodeFeeSettlement candidate,
        string leaseToken,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically claims only a due candidate that already has its deterministic immutable accepted-group
    /// receipt. A missing receipt produces expected contention without changing the settlement, so an
    /// ordinary <c>Prepared</c> intent remains eligible for its separate receipt-recording protocol.
    /// </summary>
    Task<AZOAResult<NodeFeeSettlement?>> TryClaimAcceptedAtomicGroupRecoveryAsync(
        NodeFeeSettlement candidate,
        string leaseToken,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically records one immutable, accepted two-leg chain group under the exact live settlement
    /// lease. The receipt binds the request economics to the settlement, releases the lease into
    /// reconciliation, and never completes the parent claim. An exact receipt replay returns the
    /// existing receipt; divergent evidence is a conflict.
    /// </summary>
    Task<AZOAResult<NodeFeeAtomicGroup?>> TryRecordAcceptedAtomicGroupAsync(
        NodeFeeSettlementRecoveryLease lease,
        NodeFeeAcceptedAtomicGroup acceptedGroup,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// Releases an active recovery lease into the non-terminal reconciliation state. The mutation applies
    /// only while <paramref name="lease"/> still owns the record and its lease has not expired.
    /// </summary>
    Task<AZOAResult<bool>> TryDeferToReconciliationAsync(
        NodeFeeSettlementRecoveryLease lease,
        string reason,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// Records an observed unknown or failed effect outcome and releases the exact live lease into
    /// reconciliation. This path is deliberately nonterminal: it cannot alter the paired parent
    /// idempotency record.
    /// </summary>
    Task<AZOAResult<bool>> TryRecordNonTerminalReconciliationAsync(
        NodeFeeSettlementRecoveryLease lease,
        NodeFeeSettlementEffectReconciliation reconciliation,
        string reason,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically marks both observed effects confirmed, settles the exact leased settlement, and
    /// completes its matching <c>InProgress</c> parent claim. Both non-empty effect references must
    /// be distinct. A stale lease, illegal lifecycle state, mismatched parent, or already-terminal
    /// pair returns <c>false</c> without mutating either row.
    /// </summary>
    Task<AZOAResult<bool>> TrySettlePairedAsync(
        NodeFeeSettlementRecoveryLease lease,
        NodeFeeSettlementTerminalization terminalization,
        DateTimeOffset now,
        CancellationToken ct = default);
}
