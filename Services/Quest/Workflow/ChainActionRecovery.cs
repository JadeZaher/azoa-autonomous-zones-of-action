using AZOA.WebAPI.Models.Blockchain;

namespace AZOA.WebAPI.Services.Quest.Workflow;

/// <summary>
/// The action the durable quest engine takes when a chain-action node
/// (Grant/Transfer/Swap/FungibleTokenCreate) FAILS, after reconciling the node's
/// broadcast tx against chain truth
/// (blockchain-recovery-and-portable-wallets §1.4).
///
/// <para>This replaces the engine's previous "blind retry" — which re-ran the
/// handler (re-broadcasting the mint/transfer) up to <c>RetryPolicy.MaxAttempts</c>
/// times — with "verify, then act". The double-mint hole was: attempt 1
/// broadcast and landed, but the confirmation read timed out, so attempts 2..5
/// minted again.</para>
/// </summary>
public enum ChainActionRecoveryAction
{
    /// <summary>
    /// The tx already landed (Confirmed). The effect is done — DO NOT retry.
    /// Record the node Succeeded (reconciled) and self-advance the DAG.
    /// </summary>
    AdvanceReconciled,

    /// <summary>
    /// The tx genuinely failed on-chain (FailedOnChain) — re-broadcasting is
    /// safe. Surface the failure to the saga so its retry/compensation budget
    /// owns the outcome.
    /// </summary>
    Retry,

    /// <summary>
    /// Confirmation is in-flight (Pending) or ambiguous (Unknown), OR no tx hash
    /// was ever recorded. Re-broadcasting could double-spend — park the run in
    /// <c>AwaitingReconciliation</c> for the sweep / an operator to resolve.
    /// </summary>
    ParkForReconciliation,
}

/// <summary>
/// Pure decision logic for reconcile-before-retry. Kept free of stores, sagas,
/// and providers so the safety-critical branch table is unit-testable in
/// isolation (the double-mint guard is the whole point of this track, so it must
/// be provable without a running saga).
/// </summary>
public static class ChainActionRecovery
{
    /// <summary>
    /// Decide what to do with a FAILED chain-action node.
    /// </summary>
    /// <param name="retriable">
    /// False for an invalid-config failure (<c>QuestNodeResults.Invalid</c>):
    /// nothing was broadcast and re-running can never succeed, so the node fails
    /// terminally without a chain probe (§1 invalid-mode handling). Such a node
    /// is surfaced as <see cref="ChainActionRecoveryAction.Retry"/> == false:
    /// see <see cref="DecideInvalid"/>.
    /// </param>
    /// <param name="txHash">
    /// The tx hash the handler stamped on its result, or null/empty if the
    /// handler failed before (or without) broadcasting.
    /// </param>
    /// <param name="confirmation">
    /// The verdict from <c>IBlockchainProvider.GetTransactionConfirmationAsync</c>.
    /// Ignored when <paramref name="txHash"/> is absent (nothing to probe).
    /// </param>
    public static ChainActionRecoveryAction Decide(string? txHash, ChainConfirmation confirmation)
    {
        // No tx hash ⇒ the handler failed before/without putting anything on the
        // wire. We CANNOT prove it was pre-broadcast (a crash between broadcast
        // and stamping the hash would also land here), so the only safe move is
        // to park — never blind-retry an op that MIGHT have broadcast.
        //
        // NOTE: a handler that provably never broadcasts (invalid config) routes
        // through DecideInvalid and never reaches here.
        if (string.IsNullOrWhiteSpace(txHash))
            return ChainActionRecoveryAction.ParkForReconciliation;

        return confirmation switch
        {
            // Already landed — re-running would double-mint. Reconcile to success.
            ChainConfirmation.Confirmed => ChainActionRecoveryAction.AdvanceReconciled,

            // Provably failed on-chain — re-broadcast is safe; let the saga retry.
            ChainConfirmation.FailedOnChain => ChainActionRecoveryAction.Retry,

            // In-flight or ambiguous — wait/park; the sweep re-checks. NEVER retry
            // on Unknown: that is exactly the double-spend this design prevents.
            ChainConfirmation.Pending or ChainConfirmation.Unknown
                => ChainActionRecoveryAction.ParkForReconciliation,

            _ => ChainActionRecoveryAction.ParkForReconciliation,
        };
    }
}
