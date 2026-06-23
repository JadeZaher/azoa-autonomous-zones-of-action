namespace AZOA.WebAPI.Models.Blockchain;

/// <summary>
/// Normalized confirmation verdict for a previously-broadcast transaction.
///
/// <para>Promotes the private <c>ChainVerdict</c> tri-state from
/// <c>ReconciliationService</c> to a shared, public type so the bridge/operation
/// reconciler AND the quest engine decide advance-vs-retry-vs-wait from ONE
/// source of truth (blockchain-recovery-and-portable-wallets §1.1).</para>
///
/// <para>The crucial distinction this type adds over the raw
/// <c>GetTransactionStatusAsync</c> dictionary is <see cref="Pending"/> vs
/// <see cref="Unknown"/>: a tx that is observably in-flight (mempool) is SAFE to
/// wait on; a tx that cannot be found at all is AMBIGUOUS (dropped vs never
/// broadcast vs RPC error) and must NEVER trigger an auto-action — re-submitting
/// on Unknown is exactly the double-spend the reconcile-before-retry design
/// exists to prevent.</para>
/// </summary>
public enum ChainConfirmation
{
    /// <summary>The tx is on-chain and succeeded. The effect LANDED — do not retry.</summary>
    Confirmed,

    /// <summary>The tx is on-chain and reverted/failed. Safe to retry or compensate.</summary>
    FailedOnChain,

    /// <summary>
    /// The tx is observable (e.g. in the mempool / 0 confirmations) but not yet
    /// confirmed. Still in flight — wait/park, never re-broadcast.
    /// </summary>
    Pending,

    /// <summary>
    /// The tx cannot be found, or the RPC errored. AMBIGUOUS by construction
    /// (dropped vs never-broadcast vs transient RPC failure). NEVER auto-act:
    /// park for reconciliation/operator. The conservative default for any
    /// provider that cannot positively distinguish dropped from not-yet-seen.
    /// </summary>
    Unknown
}
