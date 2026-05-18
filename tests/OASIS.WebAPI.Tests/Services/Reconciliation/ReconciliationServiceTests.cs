using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Core.Blockchain.Wormhole;
using OASIS.WebAPI.Core.Idempotency;
using OASIS.WebAPI.Data;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Idempotency;
using OASIS.WebAPI.Models.Responses;
using OASIS.WebAPI.Services.Reconciliation;
using OASIS.WebAPI.Tests.TestSupport;

namespace OASIS.WebAPI.Tests.Services.Reconciliation;

/// <summary>
/// Reconciliation tests: "kill mid-op → recovery converges to chain truth".
/// ReconciliationService only mutates via a conditional
/// <c>ExecuteUpdateAsync</c> whose predicate pins the expected current status,
/// then asserts exactly one row changed. EF-InMemory does not faithfully
/// implement that row-affected count, so a real SQLite DB (shared-cache
/// in-memory <see cref="SqliteTestContext"/>) is used. The chain is mocked; every
/// test asserts reconciliation NEVER calls any on-chain MUTATING method — it
/// strictly OBSERVES via <c>GetTransactionStatusAsync</c>.
/// </summary>
public class ReconciliationServiceTests
{
    /// <summary>
    /// A crash after the on-chain mint but before the Redeeming→Completed write
    /// leaves a row stuck at Redeeming. Reconciliation observes the confirmed
    /// mint and converges to Completed exactly once; a second pass is a no-op
    /// (the conditional UPDATE finds no Status==Redeeming row).
    /// </summary>
    [Fact]
    public async Task KillMidRedeem_ConvergesToChainTruth_Once_AndIdempotent()
    {
        using var harness = new SqliteReconHarness();
        // Stale enough to be reconciled (older than BridgeStaleAfterSeconds=60).
        var bridgeId = harness.SeedBridge(b =>
        {
            b.Status = BridgeStatus.Redeeming;
            b.TargetChain = "Algorand";
            b.MintTxHash = "mint_confirmed";
            b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
        });

        // Chain truth: the mint landed (Algorand 'confirmed' positive signal).
        harness.SetupTxStatus("mint_confirmed", new Dictionary<string, object>
        {
            ["confirmed"] = true,
            ["confirmedRound"] = 12345L
        });

        var svc = harness.CreateService();

        var report = await svc.ReconcileBridgeAsync(CancellationToken.None);

        report.Scanned.Should().Be(1);
        report.Advanced.Should().Be(1, "the chain-confirmed mint advances Redeeming→Completed");
        report.Failed.Should().Be(0);
        report.StuckFlagged.Should().Be(0);
        report.Errors.Should().Be(0);

        var advanced = harness.GetBridge(bridgeId);
        advanced.Status.Should().Be(BridgeStatus.Completed);
        advanced.CompletedAt.Should().NotBeNull();

        var second = await svc.ReconcileBridgeAsync(CancellationToken.None);
        second.Scanned.Should().Be(0, "the converged row is terminal and no longer a candidate");
        second.Advanced.Should().Be(0);
        second.Errors.Should().Be(0);

        var stillCompleted = harness.GetBridge(bridgeId);
        stillCompleted.Status.Should().Be(BridgeStatus.Completed);

        harness.AssertNoOnChainMutation();
    }

    /// <summary>
    /// An ambiguous chain status (IsError — tx not found / RPC down) never
    /// mutates the row: untouched before the hard-stuck age, and after crossing
    /// it the row is still not mutated but counted StuckFlagged for manual
    /// intervention. Reconciliation never guesses.
    /// </summary>
    [Fact]
    public async Task StuckAndChainSilent_NeverMutated_FlaggedOnlyWhenHardStuck()
    {
        using var harness = new SqliteReconHarness();

        // Provider can't say anything: IsError result (not found / RPC down).
        harness.SetupTxStatusError("mint_unknown");

        // (a) Stale (reconcilable) but NOT yet hard-stuck.
        var freshlyStuck = harness.SeedBridge(b =>
        {
            b.Status = BridgeStatus.Redeeming;
            b.TargetChain = "Algorand";
            b.MintTxHash = "mint_unknown";
            // > BridgeStaleAfterSeconds(60) but < BridgeHardStuckAfterSeconds(900).
            b.CreatedAt = DateTime.UtcNow.AddSeconds(-120);
        });

        var svc = harness.CreateService();
        var r1 = await svc.ReconcileBridgeAsync(CancellationToken.None);

        r1.Scanned.Should().Be(1);
        r1.Advanced.Should().Be(0);
        r1.Failed.Should().Be(0);
        r1.StuckFlagged.Should().Be(0, "indeterminate but not yet hard-stuck — leave untouched, don't flag");
        harness.GetBridge(freshlyStuck).Status.Should().Be(BridgeStatus.Redeeming,
            "ambiguous chain status must NEVER mutate the row");

        // (b) Same ambiguity, but now PAST the hard-stuck threshold.
        var hardStuck = harness.SeedBridge(b =>
        {
            b.Status = BridgeStatus.Redeeming;
            b.TargetChain = "Algorand";
            b.MintTxHash = "mint_unknown";
            b.CreatedAt = DateTime.UtcNow.AddSeconds(-5000); // > 900s hard-stuck
        });

        var r2 = await svc.ReconcileBridgeAsync(CancellationToken.None);

        // Both rows are still scanned (freshlyStuck still non-terminal).
        r2.Scanned.Should().Be(2);
        r2.Advanced.Should().Be(0);
        r2.Failed.Should().Be(0);
        r2.StuckFlagged.Should().Be(1, "the hard-stuck row is flagged for MANUAL INTERVENTION");

        harness.GetBridge(hardStuck).Status.Should().Be(BridgeStatus.Redeeming,
            "even hard-stuck + indeterminate is NEVER auto-mutated — only flagged");
        harness.GetBridge(freshlyStuck).Status.Should().Be(BridgeStatus.Redeeming);

        harness.AssertNoOnChainMutation();
    }

    /// <summary>
    /// An EXPLICIT on-chain negative (Solana success=false — tx on-chain and
    /// reverted) moves the row to Failed via the conditional update. Only an
    /// explicit negative, never an ambiguous one, causes an auto-Fail.
    /// </summary>
    [Fact]
    public async Task OnChainExplicitNegative_MarksFailed_ConditionalUpdateRespected()
    {
        using var harness = new SqliteReconHarness();
        var bridgeId = harness.SeedBridge(b =>
        {
            b.Status = BridgeStatus.Redeeming;
            b.TargetChain = "Solana";
            b.RedemptionTxHash = "redeem_reverted";
            b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
        });

        // Solana explicit revert: success=false (the tx is ON-chain and failed).
        harness.SetupTxStatus("redeem_reverted", new Dictionary<string, object>
        {
            ["success"] = false
        });

        var svc = harness.CreateService();
        var report = await svc.ReconcileBridgeAsync(CancellationToken.None);

        report.Scanned.Should().Be(1);
        report.Failed.Should().Be(1);
        report.Advanced.Should().Be(0);
        report.StuckFlagged.Should().Be(0);

        var failed = harness.GetBridge(bridgeId);
        failed.Status.Should().Be(BridgeStatus.Failed);
        failed.ErrorMessage.Should().NotBeNullOrEmpty();
        failed.ErrorMessage.Should().Contain("FAILED on-chain");
        failed.CompletedAt.Should().NotBeNull();

        var second = await svc.ReconcileBridgeAsync(CancellationToken.None);
        second.Scanned.Should().Be(0);
        second.Failed.Should().Be(0);
        harness.GetBridge(bridgeId).Status.Should().Be(BridgeStatus.Failed);

        harness.AssertNoOnChainMutation();
    }

    /// <summary>
    /// A non-terminal bridge tx younger than BridgeStaleAfterSeconds is ignored
    /// entirely (never scanned, never probed, never written) so reconciliation
    /// does not race a healthy live request still completing.
    /// </summary>
    [Fact]
    public async Task FreshInFlightBridge_NotScanned_NoProviderCall_NoWrite()
    {
        using var harness = new SqliteReconHarness();
        var bridgeId = harness.SeedBridge(b =>
        {
            b.Status = BridgeStatus.Redeeming;
            b.TargetChain = "Algorand";
            b.MintTxHash = "mint_in_flight";
            // YOUNGER than BridgeStaleAfterSeconds(60) — a live, healthy request.
            b.CreatedAt = DateTime.UtcNow.AddSeconds(-5);
        });

        // If it were probed this would (wrongly) confirm — it must NOT be probed.
        harness.SetupTxStatus("mint_in_flight", new Dictionary<string, object>
        {
            ["confirmed"] = true
        });

        var svc = harness.CreateService();
        var report = await svc.ReconcileBridgeAsync(CancellationToken.None);

        report.Scanned.Should().Be(0, "rows younger than the stale threshold are not candidates");
        report.Advanced.Should().Be(0);
        report.Failed.Should().Be(0);
        report.StuckFlagged.Should().Be(0);

        harness.GetBridge(bridgeId).Status.Should().Be(BridgeStatus.Redeeming,
            "a fresh in-flight row must be left strictly alone");

        harness.ProviderMock.Verify(p => p.GetTransactionStatusAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.AssertNoOnChainMutation();
    }

    /// <summary>
    /// A stale Pending op whose Parameters["TxHash"] the chain confirms advances
    /// to Completed via the conditional write; an op with no TxHash is flagged
    /// once hard-stuck but never mutated. Ops reconciliation only OBSERVES — it
    /// never re-broadcasts.
    /// </summary>
    [Fact]
    public async Task Operations_ConfirmedAdvances_NoTxHashFlagged_NeverRebroadcast()
    {
        using var harness = new SqliteReconHarness();

        // (a) Pending op WITH a confirmed on-chain tx.
        var confirmedOp = harness.SeedOperation(o =>
        {
            o.Status = OperationStatus.Pending;
            o.OperationType = "Mint";
            o.CreatedDate = DateTime.UtcNow.AddMinutes(-30);
            o.Parameters = new Dictionary<string, string>
            {
                ["TxHash"] = "op_tx_confirmed",
                ["ChainType"] = "Algorand"
            };
        });
        harness.SetupTxStatus("op_tx_confirmed", new Dictionary<string, object>
        {
            ["confirmed"] = true
        });

        // (b) AwaitingSignature op with NO TxHash, hard-stuck → flag, no mutate.
        var noHashOp = harness.SeedOperation(o =>
        {
            o.Status = OperationStatus.AwaitingSignature;
            o.OperationType = "Transfer";
            o.CreatedDate = DateTime.UtcNow.AddSeconds(-5000); // > 900 hard-stuck
            o.Parameters = new Dictionary<string, string>(); // nothing observable
        });

        var svc = harness.CreateService();
        var report = await svc.ReconcileOperationsAsync(CancellationToken.None);

        report.Scanned.Should().Be(2);
        report.Advanced.Should().Be(1, "the chain-confirmed op advances Pending→Completed");
        report.StuckFlagged.Should().Be(1, "the no-TxHash hard-stuck op is flagged, not mutated");
        report.Failed.Should().Be(0);
        report.Errors.Should().Be(0);

        var done = harness.GetOperation(confirmedOp);
        done.Status.Should().Be(OperationStatus.Completed);
        done.CompletedDate.Should().NotBeNull();

        var stillStuck = harness.GetOperation(noHashOp);
        stillStuck.Status.Should().Be(OperationStatus.AwaitingSignature,
            "no observable tx ⇒ NEVER mutate; only flag for manual intervention");
        stillStuck.CompletedDate.Should().BeNull();

        var second = await svc.ReconcileOperationsAsync(CancellationToken.None);
        second.Scanned.Should().Be(1);
        second.Advanced.Should().Be(0);
        second.StuckFlagged.Should().Be(1);
        harness.GetOperation(confirmedOp).Status.Should().Be(OperationStatus.Completed);

        harness.AssertNoOnChainMutation();
    }

    /// <summary>
    /// An orphan lock stuck Initiated/Locked with a LockTxHash: a CONFIRMED
    /// source-chain lock advances the lifecycle flag; an ambiguous hard-stuck
    /// lock is flagged for manual reversal but never mutated and funds never
    /// moved. Recovery is by OBSERVING the source chain only.
    /// </summary>
    [Fact]
    public async Task OrphanLock_ConfirmedAdvances_UnconfirmedFlagged_NeverReverses()
    {
        using var harness = new SqliteReconHarness();

        // (a) Initiated with a LockTxHash the SOURCE chain confirms → Locked.
        var confirmedLock = harness.SeedBridge(b =>
        {
            b.Status = BridgeStatus.Initiated;
            b.SourceChain = "Solana";          // probed chain for the lock phase
            b.TargetChain = "Algorand";
            b.LockTxHash = "lock_confirmed";
            b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
        });
        harness.SetupTxStatus("lock_confirmed", new Dictionary<string, object>
        {
            ["success"] = true // Solana positive confirmation
        });

        // (b) Locked, LockTxHash chain can't resolve, hard-stuck → flag, no move.
        var orphanLock = harness.SeedBridge(b =>
        {
            b.Status = BridgeStatus.Locked;
            b.SourceChain = "Solana";
            b.TargetChain = "Algorand";
            b.LockTxHash = "lock_unknown";
            b.CreatedAt = DateTime.UtcNow.AddSeconds(-5000); // hard-stuck
        });
        harness.SetupTxStatusError("lock_unknown");

        var svc = harness.CreateService();
        var report = await svc.ReconcileBridgeAsync(CancellationToken.None);

        report.Scanned.Should().Be(2);
        report.Advanced.Should().Be(1, "a chain-confirmed lock advances Initiated→Locked");
        report.StuckFlagged.Should().Be(1, "an indeterminate hard-stuck lock is flagged for manual reversal");
        report.Failed.Should().Be(0, "reconciliation never auto-fails an ambiguous lock");

        harness.GetBridge(confirmedLock).Status.Should().Be(BridgeStatus.Locked);

        var orphan = harness.GetBridge(orphanLock);
        orphan.Status.Should().Be(BridgeStatus.Locked,
            "an unresolved orphan lock is flagged but NEVER mutated");
        orphan.ErrorMessage.Should().BeNull("flagging is log/report only — no row mutation, funds untouched");

        // Funds never moved: no reversal / burn / lock / transfer on-chain.
        harness.AssertNoOnChainMutation();
    }

    // ─── HIGH-3: orphaned idempotency record settlement ───

    /// <summary>
    /// HIGH-3: a crash AFTER the on-chain mint but BEFORE CompleteAsync leaves
    /// the bridge row Redeeming AND its IdempotencyRecord stuck InProgress
    /// (poisoning every retry forever). Reconciliation observes the confirmed
    /// mint, advances Redeeming→Completed AND settles the orphaned record to
    /// Completed. A second pass is fully idempotent (record already terminal).
    /// </summary>
    [Fact]
    public async Task BridgeConfirmedTerminal_SettlesOrphanedInProgressIdempotency_AndIdempotentOnRerun()
    {
        using var harness = new SqliteReconHarness();
        const string idemKey = "bridge-redeem:br_orphan:digestabc";

        // Orphaned InProgress claim left by the dead process.
        harness.SeedIdempotency(idemKey, IdempotencyState.InProgress, "bridge-redeem");

        var bridgeId = harness.SeedBridge(b =>
        {
            b.Status = BridgeStatus.Redeeming;
            b.TargetChain = "Algorand";
            b.RedemptionTxHash = "redeem_confirmed";
            b.IdempotencyKey = idemKey;
            b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
        });
        harness.SetupTxStatus("redeem_confirmed", new Dictionary<string, object>
        {
            ["confirmed"] = true
        });

        var svc = harness.CreateService();
        var report = await svc.ReconcileBridgeAsync(CancellationToken.None);

        report.Advanced.Should().Be(1);
        report.Errors.Should().Be(0);

        harness.GetBridge(bridgeId).Status.Should().Be(BridgeStatus.Completed);

        var settled = harness.GetIdempotency(idemKey);
        settled.Should().NotBeNull();
        settled!.State.Should().Be(IdempotencyState.Completed,
            "the orphaned InProgress claim is settled once chain truth confirms the mint");
        settled.ResultPayload.Should().Be("redeem_confirmed",
            "the confirmed redeem tx hash is cached for idempotent replay");

        // Re-run: the bridge is terminal (not scanned) and the record is
        // already Completed — nothing errors, nothing changes.
        var second = await svc.ReconcileBridgeAsync(CancellationToken.None);
        second.Errors.Should().Be(0);
        harness.GetIdempotency(idemKey)!.State.Should().Be(IdempotencyState.Completed);

        harness.AssertNoOnChainMutation();
    }

    /// <summary>
    /// HIGH-3 (operation path): a chain-confirmed Pending op whose Parameters
    /// carry an explicit IdempotencyKey settles the orphaned InProgress record
    /// to Completed. (When the key is NOT resolvable the service flags manual
    /// intervention and never fabricates a key — exercised by leaving no key.)
    /// </summary>
    [Fact]
    public async Task OperationConfirmedTerminal_SettlesResolvableIdempotency_LeavesUnresolvableUntouched()
    {
        using var harness = new SqliteReconHarness();
        const string idemKey = "op:explicit-key:abc123";
        harness.SeedIdempotency(idemKey, IdempotencyState.InProgress, "Mint");

        // (a) op WITH an explicit resolvable key in Parameters.
        var keyedOp = harness.SeedOperation(o =>
        {
            o.Status = OperationStatus.Pending;
            o.OperationType = "Mint";
            o.CreatedDate = DateTime.UtcNow.AddMinutes(-30);
            o.Parameters = new Dictionary<string, string>
            {
                ["TxHash"] = "op_confirmed",
                ["ChainType"] = "Algorand",
                ["IdempotencyKey"] = idemKey,
            };
        });
        harness.SetupTxStatus("op_confirmed", new Dictionary<string, object>
        {
            ["confirmed"] = true
        });

        // (b) op WITHOUT a resolvable key — must NOT fabricate one; the
        // unrelated seeded record stays InProgress, op still advances.
        var unkeyedOp = harness.SeedOperation(o =>
        {
            o.Status = OperationStatus.Pending;
            o.OperationType = "Transfer";
            o.CreatedDate = DateTime.UtcNow.AddMinutes(-30);
            o.Parameters = new Dictionary<string, string>
            {
                ["TxHash"] = "op_confirmed",
                ["ChainType"] = "Algorand",
            };
        });

        var svc = harness.CreateService();
        var report = await svc.ReconcileOperationsAsync(CancellationToken.None);

        report.Advanced.Should().Be(2, "both confirmed ops advance to Completed");
        report.Errors.Should().Be(0);

        harness.GetOperation(keyedOp).Status.Should().Be(OperationStatus.Completed);
        harness.GetOperation(unkeyedOp).Status.Should().Be(OperationStatus.Completed);

        harness.GetIdempotency(idemKey)!.State.Should().Be(IdempotencyState.Completed,
            "the resolvable-key op settles its orphaned InProgress record");

        harness.AssertNoOnChainMutation();
    }

    /// <summary>
    /// HIGH-3 idempotency safety: a record that is ALREADY terminal
    /// (Completed/Failed) is left strictly untouched — reconciliation never
    /// re-writes a settled record, and never errors doing the check.
    /// </summary>
    [Fact]
    public async Task AlreadyTerminalIdempotencyRecord_LeftUntouched()
    {
        using var harness = new SqliteReconHarness();
        const string idemKey = "bridge-redeem:br_done:digestxyz";

        // Already settled (e.g. the original request DID complete it).
        harness.SeedIdempotency(idemKey, IdempotencyState.Completed, "bridge-redeem");
        var before = harness.GetIdempotency(idemKey)!;
        before.State.Should().Be(IdempotencyState.Completed);
        var beforeUpdatedAt = before.UpdatedAt;

        var bridgeId = harness.SeedBridge(b =>
        {
            b.Status = BridgeStatus.Redeeming;
            b.TargetChain = "Algorand";
            b.RedemptionTxHash = "redeem_confirmed2";
            b.IdempotencyKey = idemKey;
            b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
        });
        harness.SetupTxStatus("redeem_confirmed2", new Dictionary<string, object>
        {
            ["confirmed"] = true
        });

        var svc = harness.CreateService();
        var report = await svc.ReconcileBridgeAsync(CancellationToken.None);

        report.Advanced.Should().Be(1);
        report.Errors.Should().Be(0);
        harness.GetBridge(bridgeId).Status.Should().Be(BridgeStatus.Completed);

        var after = harness.GetIdempotency(idemKey)!;
        after.State.Should().Be(IdempotencyState.Completed,
            "an already-terminal record is never re-written");
        after.UpdatedAt.Should().Be(beforeUpdatedAt,
            "the settled record is skipped (GetAsync-first) — not touched at all");

        harness.AssertNoOnChainMutation();
    }

    // ─── MEDIUM-3: explicit Reversing state — reversal-in-flight not advanced ───

    /// <summary>
    /// MEDIUM-3 (explicit provenance): ReverseBridgeAsync now moves a bridge
    /// Completed→<see cref="BridgeStatus.Reversing"/> (an EXPLICIT state, not
    /// Redeeming) before the on-chain burn. Reconciliation must recognize a
    /// Reversing row as a reversal-in-flight by its state alone — NOT infer it
    /// from a CompletedAt timestamp or a "bridge-reverse" idempotency-key
    /// sniff — and must NOT advance it (advancing would mask the in-flight
    /// refund) nor mutate it: flag for MANUAL INTERVENTION and leave untouched.
    /// </summary>
    [Fact]
    public async Task ReversingState_NotAdvanced_FlaggedForManualIntervention()
    {
        using var harness = new SqliteReconHarness();

        // Reverse-in-flight: ReverseBridgeAsync moved it Completed→Reversing.
        // No CompletedAt / idempotency-key inference is needed — the explicit
        // Reversing state IS the provenance.
        var reversingId = harness.SeedBridge(b =>
        {
            b.Status = BridgeStatus.Reversing;
            b.TargetChain = "Algorand";
            b.RedemptionTxHash = "would_confirm_if_probed";
            b.IdempotencyKey = "bridge-reverse:br_x:sourceRecipient";
            b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
        });
        // If the guard failed and this were probed, it would WRONGLY confirm.
        harness.SetupTxStatus("would_confirm_if_probed", new Dictionary<string, object>
        {
            ["confirmed"] = true
        });

        var svc = harness.CreateService();
        var report = await svc.ReconcileBridgeAsync(CancellationToken.None);

        report.Scanned.Should().Be(1,
            "a Reversing row is still swept (NonTerminalBridge includes Reversing)");
        report.Advanced.Should().Be(0,
            "a Reversing row must NEVER be advanced to Completed");
        report.Failed.Should().Be(0);
        report.StuckFlagged.Should().Be(1,
            "reversal-in-flight is flagged for MANUAL INTERVENTION, not mutated");

        var row = harness.GetBridge(reversingId);
        row.Status.Should().Be(BridgeStatus.Reversing,
            "the explicit Reversing state is left untouched (masking it would lose the refund)");

        // It must not even be probed — the explicit-state guard short-circuits
        // before the chain probe.
        harness.ProviderMock.Verify(p => p.GetTransactionStatusAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.AssertNoOnChainMutation();
    }

    /// <summary>
    /// The provenance fix's whole point: a FORWARD redeem in Redeeming is
    /// NEVER mistaken for a reversal anymore. The old heuristic flagged any
    /// Redeeming row whose CompletedAt was set (or whose IdempotencyKey looked
    /// reverse-ish) as reversal-in-flight — a fresh redeem that legitimately
    /// carried a CompletedAt (or a redeem keyed off a string containing
    /// "reverse") would be wrongly frozen. With explicit state, a Redeeming row
    /// is unambiguously a forward redeem: a chain-confirmed redeem advances
    /// Redeeming→Completed exactly as before, regardless of CompletedAt /
    /// IdempotencyKey content.
    /// </summary>
    [Fact]
    public async Task ForwardRedeeming_WithCompletedAtAndReverseishKey_StillAdvances_NotFrozen()
    {
        using var harness = new SqliteReconHarness();

        // A genuine forward redeem in Redeeming that ALSO has CompletedAt set
        // and an idempotency key containing "reverse" — the exact inputs the
        // old CompletedAt/key heuristic would have FALSELY frozen as a reversal.
        var redeemId = harness.SeedBridge(b =>
        {
            b.Status = BridgeStatus.Redeeming;
            b.TargetChain = "Algorand";
            b.RedemptionTxHash = "redeem_confirmed_fwd";
            b.IdempotencyKey = "bridge-reverse-lookalike:not-a-reversal";
            b.CompletedAt = DateTime.UtcNow.AddMinutes(-10); // old heuristic trap
            b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
        });
        harness.SetupTxStatus("redeem_confirmed_fwd", new Dictionary<string, object>
        {
            ["confirmed"] = true
        });

        var svc = harness.CreateService();
        var report = await svc.ReconcileBridgeAsync(CancellationToken.None);

        report.Scanned.Should().Be(1);
        report.Advanced.Should().Be(1,
            "a forward Redeeming redeem is unambiguous now — explicit state, " +
            "no CompletedAt/key inference can freeze it");
        report.StuckFlagged.Should().Be(0,
            "a forward redeem must NOT be flagged as reversal-in-flight");

        harness.GetBridge(redeemId).Status.Should().Be(BridgeStatus.Completed);

        harness.AssertNoOnChainMutation();
    }

    // ─── SQLite harness + Moq chain doubles ───

    /// <summary>
    /// Wraps the shared shared-cache <see cref="SqliteTestContext"/> with the
    /// reconciliation options and the mocked chain (a single
    /// <see cref="IBlockchainProvider"/> behind a
    /// <see cref="IBlockchainProviderFactory"/> for both <c>GetProvider</c> and
    /// <c>GetDefaultProvider</c>).
    /// </summary>
    private sealed class SqliteReconHarness : IDisposable
    {
        private readonly SqliteTestContext _db = SqliteTestContext.SharedCacheInMemory();
        private readonly ReconciliationOptions _reconOptions;

        public Mock<IBlockchainProvider> ProviderMock { get; } = new();
        public Mock<IBlockchainProviderFactory> FactoryMock { get; } = new();

        public SqliteReconHarness()
        {
            ProviderMock.Setup(p => p.ChainType).Returns("Solana");
            FactoryMock.Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
                .Returns(ProviderMock.Object);
            FactoryMock.Setup(f => f.GetDefaultProvider())
                .Returns(ProviderMock.Object);

            // Low stale thresholds (so a 2-minute-old row IS reconcilable) and
            // a high-but-finite hard-stuck threshold so we can deterministically
            // straddle it with CreatedAt offsets.
            _reconOptions = new ReconciliationOptions
            {
                Enabled = true,
                BatchSize = 100,
                BridgeStaleAfterSeconds = 60,
                BridgeHardStuckAfterSeconds = 900,
                OperationStaleAfterSeconds = 60,
                OperationHardStuckAfterSeconds = 900,
            };
        }

        private OASISDbContext NewContext() => _db.NewContext();

        /// <summary>
        /// Mirrors the production sweep scope: ReconciliationService and the
        /// IIdempotencyStore both resolve the SAME scoped OASISDbContext. The
        /// real EF-backed <see cref="IdempotencyStore"/> is used (not a mock)
        /// so the InProgress→terminal settlement is exercised against a genuine
        /// relational store, consistent with this harness's philosophy.
        /// </summary>
        private IServiceScopeFactory CreateScopeFactory()
        {
            var services = new ServiceCollection();
            services.AddScoped<OASISDbContext>(_ => _db.NewContext());
            return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        }

        public ReconciliationService CreateService()
        {
            var ctx = NewContext();
            return new ReconciliationService(
                ctx,
                FactoryMock.Object,
                new IdempotencyStore(CreateScopeFactory()),
                Mock.Of<ILogger<ReconciliationService>>(),
                Options.Create(_reconOptions));
        }

        /// <summary>Seed an idempotency record in a given state (models the
        /// orphaned InProgress claim a crash leaves behind).</summary>
        public void SeedIdempotency(
            string key, IdempotencyState state, string operationType = "bridge-redeem")
        {
            using var db = NewContext();
            db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Key = key,
                OperationType = operationType,
                State = state,
                CreatedAt = DateTime.UtcNow.AddMinutes(-30),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-30),
            });
            db.SaveChanges();
        }

        public IdempotencyRecord? GetIdempotency(string key)
        {
            using var db = NewContext();
            return db.IdempotencyRecords.AsNoTracking()
                .FirstOrDefault(r => r.Key == key);
        }

        /// <summary>Configure the chain probe for a tx hash to return a
        /// successful <c>OASISResult</c> carrying the given status dictionary.</summary>
        public void SetupTxStatus(string txHash, Dictionary<string, object> status) =>
            ProviderMock
                .Setup(p => p.GetTransactionStatusAsync(txHash, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OASISResult<Dictionary<string, object>>
                {
                    IsError = false,
                    Result = status
                });

        /// <summary>Configure the chain probe for a tx hash to return an
        /// errored <c>OASISResult</c> (tx not found / RPC error — ambiguous).</summary>
        public void SetupTxStatusError(string txHash) =>
            ProviderMock
                .Setup(p => p.GetTransactionStatusAsync(txHash, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OASISResult<Dictionary<string, object>>
                {
                    IsError = true,
                    Message = "transaction not found",
                    Result = null
                });

        public string SeedBridge(Action<BridgeTransactionResult> configure)
        {
            var row = new BridgeTransactionResult
            {
                Id = $"br_{Guid.NewGuid():N}",
                AvatarId = Guid.NewGuid(),
                SourceChain = "Solana",
                TargetChain = "Algorand",
                SourceTokenId = "token1",
                SourceAddress = "src",
                TargetAddress = "dst",
                Amount = 1,
                Mode = BridgeMode.Wormhole,
                Status = BridgeStatus.Redeeming,
                CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            };
            configure(row);
            using var db = NewContext();
            db.BridgeTransactions.Add(row);
            db.SaveChanges();
            return row.Id;
        }

        public Guid SeedOperation(Action<BlockchainOperation> configure)
        {
            var op = new BlockchainOperation
            {
                Id = Guid.NewGuid(),
                AvatarId = Guid.NewGuid(),
                WalletId = Guid.NewGuid(),
                OperationType = "Mint",
                Status = OperationStatus.Pending,
                CreatedDate = DateTime.UtcNow.AddMinutes(-30),
                Parameters = new Dictionary<string, string>(),
            };
            configure(op);
            using var db = NewContext();
            db.BlockchainOperations.Add(op);
            db.SaveChanges();
            return op.Id;
        }

        public BridgeTransactionResult GetBridge(string id)
        {
            using var db = NewContext();
            return db.BridgeTransactions.AsNoTracking().Single(b => b.Id == id);
        }

        public BlockchainOperation GetOperation(Guid id)
        {
            using var db = NewContext();
            return db.BlockchainOperations.AsNoTracking().Single(o => o.Id == id);
        }

        /// <summary>The central safety invariant: reconciliation OBSERVES, it
        /// NEVER broadcasts/mutates on-chain.</summary>
        public void AssertNoOnChainMutation()
        {
            ProviderMock.Verify(p => p.MintAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            ProviderMock.Verify(p => p.MintWrappedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            ProviderMock.Verify(p => p.BurnAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
            ProviderMock.Verify(p => p.BurnWrappedAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            ProviderMock.Verify(p => p.TransferAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
            ProviderMock.Verify(p => p.SwapAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>(),
                It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            ProviderMock.Verify(p => p.ExchangeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            ProviderMock.Verify(p => p.LockForBridgeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            ProviderMock.Verify(p => p.DeployContractAsync(
                It.IsAny<byte[]>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()), Times.Never);
            ProviderMock.Verify(p => p.CallContractAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        public void Dispose() => _db.Dispose();
    }
}
