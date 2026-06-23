using FluentAssertions;
using AZOA.WebAPI.Core.Blockchain;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Quest.Workflow;
using Xunit;

namespace AZOA.WebAPI.Tests.Services.Quest;

/// <summary>
/// Prototype for blockchain-recovery-and-portable-wallets §1.4 — the
/// reconcile-before-retry decision table. These tests pin the SAFETY-CRITICAL
/// invariant of the track: a chain-action node that failed AFTER its tx landed
/// must NOT be retried (no double-mint), and an ambiguous chain result must NEVER
/// trigger a blind re-broadcast.
/// </summary>
public class ChainActionRecoveryTests
{
    // ───────────────────────── The double-mint guard ─────────────────────────

    [Fact]
    public void ConfirmedTx_AdvancesReconciled_NeverRetries()
    {
        // The headline guard: handler reported failure (e.g. confirmation read
        // timed out) but the mint ACTUALLY LANDED. Retrying would mint again.
        var action = ChainActionRecovery.Decide("TX_THAT_LANDED", ChainConfirmation.Confirmed);

        action.Should().Be(ChainActionRecoveryAction.AdvanceReconciled);
        action.Should().NotBe(ChainActionRecoveryAction.Retry,
            "a confirmed tx must never be re-broadcast — that is the double-mint hole");
    }

    [Fact]
    public void FailedOnChainTx_Retries()
    {
        // Provably failed on-chain ⇒ re-broadcast is safe; hand back to the saga.
        var action = ChainActionRecovery.Decide("TX_REVERTED", ChainConfirmation.FailedOnChain);

        action.Should().Be(ChainActionRecoveryAction.Retry);
    }

    [Theory]
    [InlineData(ChainConfirmation.Pending)]
    [InlineData(ChainConfirmation.Unknown)]
    public void PendingOrUnknownTx_ParksForReconciliation_NeverRetries(ChainConfirmation verdict)
    {
        // In-flight or ambiguous ⇒ a re-broadcast could double-spend. Park.
        var action = ChainActionRecovery.Decide("TX_IN_LIMBO", verdict);

        action.Should().Be(ChainActionRecoveryAction.ParkForReconciliation);
        action.Should().NotBe(ChainActionRecoveryAction.Retry,
            "an ambiguous chain result must never blind-retry");
    }

    [Fact]
    public void NoTxHash_ParksForReconciliation()
    {
        // No recorded hash ⇒ we cannot prove the handler was pre-broadcast (a
        // crash between broadcast and stamping lands here too). Park, never retry.
        ChainActionRecovery.Decide(null, ChainConfirmation.Unknown)
            .Should().Be(ChainActionRecoveryAction.ParkForReconciliation);
        ChainActionRecovery.Decide("", ChainConfirmation.Confirmed)
            .Should().Be(ChainActionRecoveryAction.ParkForReconciliation,
                "without a hash there is nothing to have confirmed — the verdict is moot");
    }

    // ─────────────────── The shared classifier (base default) ────────────────

    [Fact]
    public void Classify_AlgorandConfirmed_IsConfirmed()
    {
        var ok = new AZOAResult<Dictionary<string, object>>
        {
            Result = new Dictionary<string, object> { ["confirmed"] = true }
        };
        ChainTxClassifier.Classify(ok).Should().Be(ChainConfirmation.Confirmed);
    }

    [Fact]
    public void Classify_AlgorandZeroRounds_IsPending_NotUnknown()
    {
        // A non-error dictionary with confirmed==false means the tx WAS found but
        // hasn't reached a confirming round — that is Pending (in-flight), the
        // sharper tri-state this track adds over the reconciler's old Unknown.
        var notYet = new AZOAResult<Dictionary<string, object>>
        {
            Result = new Dictionary<string, object> { ["confirmed"] = false }
        };
        ChainTxClassifier.Classify(notYet).Should().Be(ChainConfirmation.Pending);
    }

    [Fact]
    public void Classify_SolanaRevert_IsFailedOnChain()
    {
        var reverted = new AZOAResult<Dictionary<string, object>>
        {
            Result = new Dictionary<string, object> { ["success"] = false }
        };
        ChainTxClassifier.Classify(reverted).Should().Be(ChainConfirmation.FailedOnChain);
    }

    [Fact]
    public void Classify_ErrorOrNotFound_IsUnknown_NeverFailedOnChain()
    {
        // The conservative invariant: a not-found / RPC-error probe is AMBIGUOUS
        // and must map to Unknown, never FailedOnChain (which would wrongly green-
        // light a re-broadcast).
        var errored = new AZOAResult<Dictionary<string, object>> { IsError = true };
        ChainTxClassifier.Classify(errored).Should().Be(ChainConfirmation.Unknown);

        var nullResult = new AZOAResult<Dictionary<string, object>> { Result = null };
        ChainTxClassifier.Classify(nullResult).Should().Be(ChainConfirmation.Unknown);
    }

    [Fact]
    public void Classify_UnrecognizedShape_IsUnknown()
    {
        var weird = new AZOAResult<Dictionary<string, object>>
        {
            Result = new Dictionary<string, object> { ["somethingElse"] = 42 }
        };
        ChainTxClassifier.Classify(weird).Should().Be(ChainConfirmation.Unknown);
    }

    // ───────── End-to-end: classifier verdict drives the recovery action ──────

    [Fact]
    public void ErroredProbe_OnAFailedNode_Parks_DoesNotRetry()
    {
        // The realistic double-mint scenario: the mint broadcast, but the status
        // probe errors (RPC blip). Classifier ⇒ Unknown ⇒ recovery ⇒ Park.
        var probe = new AZOAResult<Dictionary<string, object>> { IsError = true };
        var verdict = ChainTxClassifier.Classify(probe);

        ChainActionRecovery.Decide("TX_BROADCAST_THEN_RPC_BLIP", verdict)
            .Should().Be(ChainActionRecoveryAction.ParkForReconciliation);
    }
}
