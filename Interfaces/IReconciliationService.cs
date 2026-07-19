namespace AZOA.WebAPI.Interfaces;

/// <summary>
/// Re-derives the TRUE status of non-terminal bridge transactions and
/// blockchain operations from on-chain confirmations, instead of trusting the
/// local lifecycle flag.
///
/// <para>
/// Spec context (api-safety-hardening, "Chain reconciliation", plan tasks
/// 14/15): "op/bridge status is a local lifecycle flag, never re-derived from
/// chain confirmations. A crash mid-flight leaves an op neither safely
/// retriable nor known-complete." This service closes that gap.
/// </para>
///
/// <para>
/// SAFETY INVARIANTS (enforced by the implementation):
/// <list type="bullet">
/// <item>Reconciliation OBSERVES chain truth; it NEVER performs an on-chain
/// mutation (no re-broadcast, no fund reversal, no mint/redeem).</item>
/// <item>Every status write is a single conditional <c>ExecuteUpdateAsync</c>
/// whose predicate includes the expected current status; the implementation
/// asserts exactly one row changed. Safe to run concurrently with live
/// requests and with itself (idempotent passes).</item>
/// <item>Ambiguous chain results (tx not found / RPC error / unknown) NEVER
/// cause a status change — the record is left as-is and flagged for manual
/// intervention once it crosses the hard-stuck threshold.</item>
/// </list>
/// </para>
///
/// Scoped lifetime (resolves the per-aggregate stores). The background sweep
/// creates a DI scope per tick — see <c>ReconciliationHostedService</c>.
/// </summary>
public interface IReconciliationService
{
    /// <summary>
    /// Scan non-terminal bridge transactions older than the configured
    /// staleness threshold and re-derive their status from chain truth.
    /// </summary>
    Task<ReconciliationReport> ReconcileBridgeAsync(CancellationToken ct);

    /// <summary>
    /// Scan blockchain operations stuck in a non-terminal status
    /// (Pending / AwaitingSignature) past the staleness threshold and
    /// re-derive their status from chain truth.
    /// </summary>
    Task<ReconciliationReport> ReconcileOperationsAsync(CancellationToken ct);

    /// <summary>
    /// Reconcile one blockchain operation by id for an authenticated caller's
    /// receipt lookup. This bypasses the staleness filter and observes chain
    /// truth only; it never broadcasts, retries, or otherwise submits a value
    /// operation. A locally terminal operation is settled only after a matching
    /// positive or explicitly negative on-chain observation.
    /// </summary>
    /// <param name="id">The durable operation identifier to observe.</param>
    /// <param name="ct">Cancellation token for the read-only reconciliation pass.</param>
    Task<ReconciliationReport> ReconcileOperationAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Reconcile a single bridge transaction by id (manual / targeted trigger,
    /// e.g. from an ops runbook). Bypasses the staleness filter; still honours
    /// every safety invariant above.
    /// </summary>
    Task<ReconciliationReport> ReconcileBridgeTransactionAsync(string id, CancellationToken ct);
}

/// <summary>
/// Outcome counts for one reconciliation pass. Immutable; combine passes with
/// <see cref="Combine"/>.
/// </summary>
public sealed record ReconciliationReport(
    int Scanned,
    int Advanced,
    int StuckFlagged,
    int Failed,
    int Errors,
    int LockedFundsAtRisk = 0)
{
    public static ReconciliationReport Empty { get; } = new(0, 0, 0, 0, 0);

    /// <summary>Number of records that crossed the hard-stuck threshold with
    /// no derivable chain truth and were flagged "MANUAL INTERVENTION
    /// REQUIRED" (logged + counted, never mutated).</summary>
    public ReconciliationReport Combine(ReconciliationReport other) => new(
        Scanned + other.Scanned,
        Advanced + other.Advanced,
        StuckFlagged + other.StuckFlagged,
        Failed + other.Failed,
        Errors + other.Errors,
        Math.Max(LockedFundsAtRisk, other.LockedFundsAtRisk));

    public override string ToString() =>
        $"scanned={Scanned} advanced={Advanced} stuckFlagged={StuckFlagged} " +
        $"failed={Failed} errors={Errors} lockedFundsAtRisk={LockedFundsAtRisk}";
}
