using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Bridge;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Reconciliation;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Services;

/// <summary>
/// Unit tests for the G1/G5/G6/Window-6 hardening changes in
/// <see cref="ReconciliationService"/>. Drives the service directly with mocks
/// following the <c>FakeReconHarness</c> convention from
/// <see cref="Reconciliation.ReconciliationServiceTests"/>.
///
/// See conductor/tracks/bridge-safety-hardening/plan.md Phase C item 7.
/// </summary>
public class ReconciliationBridgeHardeningTests
{
    // ── Test 1: G1 — Locked + Confirmed → NO transition, StuckFlagged = 1 ──────

    /// <summary>
    /// G1: a Locked row whose LockTxHash the chain reports Confirmed must NOT
    /// attempt a Locked→Locked self-transition. The sweep logs a warning and
    /// returns StuckFlagged=1 without calling TryTransitionBridgeStatusAsync.
    /// The row stays Locked; no mutation occurs.
    /// </summary>
    [Fact]
    public async Task G1_LockedWithConfirmedLockTx_NoSelfTransition_StuckFlagged()
    {
        using var harness = new BridgeHardeningHarness();

        var bridgeId = harness.SeedBridge(b =>
        {
            b.Status = BridgeStatus.Locked;
            b.SourceChain = "Solana";
            b.TargetChain = "Algorand";
            b.LockTxHash = "lock_confirmed_g1";
            b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
        });

        // Chain reports the lock tx as confirmed (Solana success=true).
        harness.SetupTxStatus("lock_confirmed_g1", new Dictionary<string, object>
        {
            ["success"] = true
        });

        var svc = harness.CreateService();
        var report = await svc.ReconcileBridgeAsync(CancellationToken.None);

        report.Scanned.Should().Be(1);
        report.StuckFlagged.Should().Be(1, "G1: confirmed lock on Locked row must flag instead of self-transitioning");
        report.Advanced.Should().Be(0, "no forward advance may happen");
        report.Failed.Should().Be(0);
        report.Errors.Should().Be(0);

        var row = harness.GetBridge(bridgeId);
        row.Status.Should().Be(BridgeStatus.Locked, "the row must remain Locked — no self-transition");

        // The critical safety assertion: TryTransitionBridgeStatusAsync must NEVER have been called.
        harness.BridgeStoreMock.Verify(
            s => s.TryTransitionBridgeStatusAsync(
                It.IsAny<string>(), It.IsAny<BridgeStatus>(), It.IsAny<BridgeStatus>(),
                It.IsAny<BridgeStatusMutation?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "G1: the Locked→Locked self-transition must never be attempted");

        harness.AssertNoOnChainMutation();
    }

    // ── Test 2: G1 — Locked + FailedOnChain → Locked→Failed transition once ───

    /// <summary>
    /// G1 (negative branch): a Locked row whose LockTxHash is reported
    /// FailedOnChain still drives Locked→Failed exactly once. The G1 guard
    /// only blocks the self-transition on Confirmed; FailedOnChain falls
    /// through to the normal Failed path.
    /// </summary>
    [Fact]
    public async Task G1_LockedWithFailedOnChainLockTx_TransitionsToFailed_ExactlyOnce()
    {
        using var harness = new BridgeHardeningHarness();

        var bridgeId = harness.SeedBridge(b =>
        {
            b.Status = BridgeStatus.Locked;
            b.SourceChain = "Solana";
            b.TargetChain = "Algorand";
            b.LockTxHash = "lock_failed_g1";
            b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
        });

        // Solana explicit revert: success=false → FailedOnChain verdict.
        harness.SetupTxStatus("lock_failed_g1", new Dictionary<string, object>
        {
            ["success"] = false
        });

        var svc = harness.CreateService();
        var report = await svc.ReconcileBridgeAsync(CancellationToken.None);

        report.Scanned.Should().Be(1);
        report.Failed.Should().Be(1, "G1: FailedOnChain on Locked must still drive Locked→Failed");
        report.Advanced.Should().Be(0);
        report.StuckFlagged.Should().Be(0);
        report.Errors.Should().Be(0);

        var row = harness.GetBridge(bridgeId);
        row.Status.Should().Be(BridgeStatus.Failed);
        row.ErrorMessage.Should().NotBeNullOrEmpty();
        row.CompletedAt.Should().NotBeNull();

        // Second pass: row is terminal — not scanned again.
        var second = await svc.ReconcileBridgeAsync(CancellationToken.None);
        second.Scanned.Should().Be(0);
        second.Failed.Should().Be(0);

        harness.AssertNoOnChainMutation();
    }

    // ── Test 3: Regression — Initiated + Confirmed → Initiated→Locked ─────────

    /// <summary>
    /// Regression guard: an Initiated row with a confirmed LockTxHash must
    /// still advance Initiated→Locked. The G1 guard only fires when BOTH the
    /// current status IS Locked AND the advanceTo IS Locked; Initiated rows are
    /// unaffected.
    /// </summary>
    [Fact]
    public async Task Regression_InitiatedWithConfirmedLock_AdvancesToLocked()
    {
        using var harness = new BridgeHardeningHarness();

        var bridgeId = harness.SeedBridge(b =>
        {
            b.Status = BridgeStatus.Initiated;
            b.SourceChain = "Solana";
            b.TargetChain = "Algorand";
            b.LockTxHash = "lock_confirmed_initiated";
            b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
        });

        harness.SetupTxStatus("lock_confirmed_initiated", new Dictionary<string, object>
        {
            ["success"] = true
        });

        var svc = harness.CreateService();
        var report = await svc.ReconcileBridgeAsync(CancellationToken.None);

        report.Scanned.Should().Be(1);
        report.Advanced.Should().Be(1, "Initiated with confirmed lock must advance to Locked");
        report.StuckFlagged.Should().Be(0);
        report.Failed.Should().Be(0);

        harness.GetBridge(bridgeId).Status.Should().Be(BridgeStatus.Locked);

        harness.AssertNoOnChainMutation();
    }

    // ── Test 4: Regression — VAAReady/Redeeming + Confirmed → Completed ────────

    /// <summary>
    /// Regression guard: VAAReady and Redeeming rows with a confirmed redeem/mint
    /// tx must still be driven to Completed with their idempotency records settled.
    /// G1 only guards the Locked self-transition; these paths are unaffected.
    /// </summary>
    [Fact]
    public async Task Regression_VAAReadyAndRedeemingWithConfirmedTx_DrivenToCompleted_IdempotencySettled()
    {
        using var harness = new BridgeHardeningHarness();

        const string vaaReadyKey = "idem:vaaready:regression";
        const string redeemingKey = "idem:redeeming:regression";

        harness.SeedIdempotency(vaaReadyKey, IdempotencyState.InProgress, "bridge-redeem");
        harness.SeedIdempotency(redeemingKey, IdempotencyState.InProgress, "bridge-redeem");

        var vaaReadyId = harness.SeedBridge(b =>
        {
            b.Status = BridgeStatus.VAAReady;
            b.TargetChain = "Algorand";
            b.MintTxHash = "mint_confirmed_vaaready";
            b.IdempotencyKey = vaaReadyKey;
            b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
        });

        var redeemingId = harness.SeedBridge(b =>
        {
            b.Status = BridgeStatus.Redeeming;
            b.TargetChain = "Algorand";
            b.RedemptionTxHash = "redeem_confirmed_redeeming";
            b.IdempotencyKey = redeemingKey;
            b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
        });

        harness.SetupTxStatus("mint_confirmed_vaaready", new Dictionary<string, object>
        {
            ["confirmed"] = true
        });
        harness.SetupTxStatus("redeem_confirmed_redeeming", new Dictionary<string, object>
        {
            ["confirmed"] = true
        });

        var svc = harness.CreateService();
        var report = await svc.ReconcileBridgeAsync(CancellationToken.None);

        report.Scanned.Should().Be(2);
        report.Advanced.Should().Be(2, "both VAAReady and Redeeming must advance to Completed");
        report.StuckFlagged.Should().Be(0);
        report.Failed.Should().Be(0);

        harness.GetBridge(vaaReadyId).Status.Should().Be(BridgeStatus.Completed);
        harness.GetBridge(redeemingId).Status.Should().Be(BridgeStatus.Completed);

        harness.GetIdempotency(vaaReadyKey)!.State.Should().Be(IdempotencyState.Completed,
            "VAAReady idempotency record must be settled to Completed");
        harness.GetIdempotency(redeemingKey)!.State.Should().Be(IdempotencyState.Completed,
            "Redeeming idempotency record must be settled to Completed");

        harness.AssertNoOnChainMutation();
    }

    // ── Test 5: Reversing → StuckFlagged, zero provider calls, zero transitions ─

    /// <summary>
    /// A Reversing row is an in-flight refund. The service must short-circuit
    /// before any provider call, flag it for manual intervention (StuckFlagged=1),
    /// and leave the row untouched with zero on-chain probes.
    /// </summary>
    [Fact]
    public async Task ReversingRow_StuckFlagged_ZeroProviderCalls_ZeroTransitions()
    {
        using var harness = new BridgeHardeningHarness();

        var reversingId = harness.SeedBridge(b =>
        {
            b.Status = BridgeStatus.Reversing;
            b.TargetChain = "Algorand";
            b.RedemptionTxHash = "would_probe_if_not_guarded";
            b.IdempotencyKey = "bridge-reverse:br_rev:key";
            b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
        });

        // If the Reversing guard were absent, this tx would wrongly confirm.
        harness.SetupTxStatus("would_probe_if_not_guarded", new Dictionary<string, object>
        {
            ["confirmed"] = true
        });

        var svc = harness.CreateService();
        var report = await svc.ReconcileBridgeAsync(CancellationToken.None);

        report.Scanned.Should().Be(1);
        report.StuckFlagged.Should().Be(1, "a Reversing row must be flagged for manual intervention");
        report.Advanced.Should().Be(0);
        report.Failed.Should().Be(0);
        report.Errors.Should().Be(0);

        harness.GetBridge(reversingId).Status.Should().Be(BridgeStatus.Reversing,
            "the Reversing row must not be mutated");

        // The explicit-state guard must short-circuit before any provider call.
        harness.ProviderMock.Verify(
            p => p.GetTransactionStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "provider must not be probed for a Reversing row");

        harness.BridgeStoreMock.Verify(
            s => s.TryTransitionBridgeStatusAsync(
                It.IsAny<string>(), It.IsAny<BridgeStatus>(), It.IsAny<BridgeStatus>(),
                It.IsAny<BridgeStatusMutation?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no transition must be attempted on a Reversing row");

        harness.AssertNoOnChainMutation();
    }

    // ── Test 6: G5 — Network field drives provider resolution ─────────────────

    /// <summary>
    /// G5: the factory is called with the network stamped on the bridge row.
    /// Network=Testnet → GetProvider receives ChainNetwork.Testnet.
    /// Network=null   → GetProvider falls back to ChainNetwork.Devnet.
    /// </summary>
    [Fact]
    public async Task G5_NetworkFieldDrivesProviderResolution_NullFallsBackToDevnet()
    {
        // ── Testnet row ──────────────────────────────────────────────────────────
        {
            using var harness = new BridgeHardeningHarness();

            harness.SeedBridge(b =>
            {
                b.Status = BridgeStatus.Redeeming;
                b.TargetChain = "Algorand";
                b.RedemptionTxHash = "redeem_testnet";
                b.Network = ChainNetwork.Testnet;
                b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
            });

            harness.SetupTxStatus("redeem_testnet", new Dictionary<string, object>
            {
                ["confirmed"] = true
            });

            ChainNetwork? capturedNetwork = null;
            harness.FactoryMock
                .Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
                .Callback<string, ChainNetwork>((_, n) => capturedNetwork = n)
                .Returns(harness.ProviderMock.Object);

            var svc = harness.CreateService();
            await svc.ReconcileBridgeAsync(CancellationToken.None);

            capturedNetwork.Should().Be(ChainNetwork.Testnet,
                "G5: factory must receive the network stamped on the row (Testnet)");
        }

        // ── Null-network row → Devnet fallback ───────────────────────────────────
        {
            using var harness = new BridgeHardeningHarness();

            harness.SeedBridge(b =>
            {
                b.Status = BridgeStatus.Redeeming;
                b.TargetChain = "Algorand";
                b.RedemptionTxHash = "redeem_nullnetwork";
                b.Network = null; // pre-field row — must fall back to Devnet
                b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
            });

            harness.SetupTxStatus("redeem_nullnetwork", new Dictionary<string, object>
            {
                ["confirmed"] = true
            });

            ChainNetwork? capturedNetwork = null;
            harness.FactoryMock
                .Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
                .Callback<string, ChainNetwork>((_, n) => capturedNetwork = n)
                .Returns(harness.ProviderMock.Object);

            var svc = harness.CreateService();
            await svc.ReconcileBridgeAsync(CancellationToken.None);

            capturedNetwork.Should().Be(ChainNetwork.Devnet,
                "G5: null Network on the row must fall back to Devnet for pre-field rows");
        }
    }

    // ── Test 7: G6 — GetFailedBridgesWithLockedFundsAsync drives report field ──

    /// <summary>
    /// G6: when <c>GetFailedBridgesWithLockedFundsAsync</c> returns N ids, the
    /// sweep calls it exactly once, logs a warning, and the report's
    /// <c>LockedFundsAtRisk</c> equals N. <c>Combine</c> preserves the value
    /// via <c>Math.Max</c>. An empty result produces zero.
    /// </summary>
    [Fact]
    public async Task G6_LockedFundsAtRisk_ReflectsGetFailedBridgesCount_CombinePreservesViaMax()
    {
        // ── Non-empty: 3 ids ────────────────────────────────────────────────────
        {
            using var harness = new BridgeHardeningHarness();

            // Seed 3 Failed bridges with LockTxHash but no MintTxHash.
            for (int i = 0; i < 3; i++)
            {
                harness.SeedBridge(b =>
                {
                    b.Status = BridgeStatus.Failed;
                    b.LockTxHash = $"lock_stuck_{Guid.NewGuid():N}";
                    b.MintTxHash = null;
                    b.CreatedAt = DateTime.UtcNow.AddMinutes(-60);
                });
            }

            // No non-terminal rows — sweep candidates are empty, G6 check runs alone.
            var svc = harness.CreateService();
            var report = await svc.ReconcileBridgeAsync(CancellationToken.None);

            report.LockedFundsAtRisk.Should().Be(3,
                "G6: report.LockedFundsAtRisk must equal the count of failed bridges with locked funds");
            report.Errors.Should().Be(0);

            // Run again — Combine applies Math.Max; the field is cumulative across
            // the per-id sub-reports and the G6 check report.
            var second = await svc.ReconcileBridgeAsync(CancellationToken.None);
            second.LockedFundsAtRisk.Should().Be(3,
                "Combine uses Math.Max — a second sweep with the same 3 ids still reports 3");
        }

        // ── Empty: zero ─────────────────────────────────────────────────────────
        {
            using var harness = new BridgeHardeningHarness();
            // No failed+locked bridges seeded.
            var svc = harness.CreateService();
            var report = await svc.ReconcileBridgeAsync(CancellationToken.None);

            report.LockedFundsAtRisk.Should().Be(0,
                "G6: empty result from GetFailedBridgesWithLockedFundsAsync → LockedFundsAtRisk = 0");
        }
    }

    // ── Test 8: Window-6 — Terminal re-read race settlement ────────────────────

    /// <summary>
    /// Window-6: <c>ReconcileOneBridgeAsync</c> re-reads the row fresh; if it
    /// has already reached a terminal state (raced with a foreground request)
    /// and carries an IdempotencyKey, the sweep must settle the idempotency
    /// record before returning. Three sub-cases:
    ///   (a) Completed + key → IIdempotencyStore.CompleteAsync called.
    ///   (b) Failed     + key → IIdempotencyStore.FailAsync called.
    ///   (c) Terminal         with NO key → neither CompleteAsync nor FailAsync called.
    /// </summary>
    [Fact]
    public async Task Window6_TerminalRereadRace_SettlesIdempotencyOnlyWhenKeyPresent()
    {
        // ── (a) Completed + key → CompleteAsync ──────────────────────────────────
        {
            using var harness = new BridgeHardeningHarness();
            const string key = "idem:window6:completed";
            harness.SeedIdempotency(key, IdempotencyState.InProgress, "bridge-redeem");

            // The row is seeded as TERMINAL (Completed) right from the start.
            // ReconcileOneBridgeAsync will re-read it, see it is non-NonTerminal, and
            // enter the Window-6 branch.
            // To get it into the sweep, we seed it as stale non-terminal first, then
            // swap it to Completed before the service reads it by seeding a Completed row
            // directly. The service uses GetNonTerminalBridgeIdsAsync to build the
            // candidate list — a Completed row won't appear there. To exercise Window-6
            // we call ReconcileBridgeTransactionAsync (manual trigger) which bypasses
            // the staleness filter and always re-reads.
            harness.SeedBridge(b =>
            {
                b.Status = BridgeStatus.Completed;
                b.TargetChain = "Algorand";
                b.RedemptionTxHash = "already_done";
                b.IdempotencyKey = key;
                b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
                b.CompletedAt = DateTime.UtcNow.AddMinutes(-1);
            });

            var bridgeId = harness.LastSeededId;
            var svc = harness.CreateService();

            // Manual trigger — bypasses staleness filter, drives Window-6 directly.
            var report = await svc.ReconcileBridgeTransactionAsync(bridgeId, CancellationToken.None);

            report.Errors.Should().Be(0);

            var settled = harness.GetIdempotency(key);
            settled.Should().NotBeNull();
            settled!.State.Should().Be(IdempotencyState.Completed,
                "Window-6: Completed terminal row + key → CompleteAsync must be called");

            harness.AssertNoOnChainMutation();
        }

        // ── (b) Failed + key → FailAsync ─────────────────────────────────────────
        {
            using var harness = new BridgeHardeningHarness();
            const string key = "idem:window6:failed";
            harness.SeedIdempotency(key, IdempotencyState.InProgress, "bridge-redeem");

            harness.SeedBridge(b =>
            {
                b.Status = BridgeStatus.Failed;
                b.TargetChain = "Algorand";
                b.IdempotencyKey = key;
                b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
                b.CompletedAt = DateTime.UtcNow.AddMinutes(-1);
            });

            var bridgeId = harness.LastSeededId;
            var svc = harness.CreateService();
            var report = await svc.ReconcileBridgeTransactionAsync(bridgeId, CancellationToken.None);

            report.Errors.Should().Be(0);

            var settled = harness.GetIdempotency(key);
            settled!.State.Should().Be(IdempotencyState.Failed,
                "Window-6: Failed terminal row + key → FailAsync must be called");

            harness.AssertNoOnChainMutation();
        }

        // ── (c) Terminal WITHOUT key → no CompleteAsync / FailAsync ──────────────
        {
            using var harness = new BridgeHardeningHarness();

            harness.SeedBridge(b =>
            {
                b.Status = BridgeStatus.Completed;
                b.TargetChain = "Algorand";
                b.IdempotencyKey = null; // no key — cannot settle without guessing
                b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
                b.CompletedAt = DateTime.UtcNow.AddMinutes(-1);
            });

            var bridgeId = harness.LastSeededId;
            var svc = harness.CreateService();
            var report = await svc.ReconcileBridgeTransactionAsync(bridgeId, CancellationToken.None);

            report.Errors.Should().Be(0);

            // The idempotency store must not have had CompleteAsync or FailAsync called
            // (it has no record at all — verify it remains absent).
            harness.IdempotencyStoreMock.Verify(
                s => s.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "Window-6: terminal without key must not call CompleteAsync");
            harness.IdempotencyStoreMock.Verify(
                s => s.FailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "Window-6: terminal without key must not call FailAsync");

            harness.AssertNoOnChainMutation();
        }
    }

    // ── Test 9: Strict provider — sweep never calls mutating methods ───────────

    /// <summary>
    /// Safety invariant: across a sweep of mixed-status non-terminal rows (each
    /// with an observable tx hash), the provider is configured with
    /// MockBehavior.Strict with only GetTransactionStatusAsync allowed. Any call
    /// to a mutating method (MintAsync, BurnAsync, etc.) would throw — proving
    /// the sweep is purely observational.
    /// </summary>
    [Fact]
    public async Task Sweep_NeverCallsMutatingProviderMethods_StrictMockEnforces()
    {
        // Use a strict mock — any unexpected call throws MockException.
        var strictProvider = new Mock<IBlockchainProvider>(MockBehavior.Strict);
        strictProvider.Setup(p => p.ChainType).Returns("Algorand");

        var statuses = new[]
        {
            BridgeStatus.Initiated,
            BridgeStatus.Locked,
            BridgeStatus.Redeeming,
        };

        using var harness = new BridgeHardeningHarness(strictProvider);

        foreach (var status in statuses)
        {
            var txHash = $"tx_{status}_{Guid.NewGuid():N}";
            harness.SeedBridge(b =>
            {
                b.Status = status;
                b.SourceChain = "Algorand";
                b.TargetChain = "Algorand";
                b.LockTxHash = status == BridgeStatus.Redeeming ? null : txHash;
                b.RedemptionTxHash = status == BridgeStatus.Redeeming ? txHash : null;
                b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
            });

            // GetTransactionStatusAsync is the ONLY allowed call — returns Unknown
            // so no transitions fire (avoids idempotency settle calls that would
            // also require store setup).
            strictProvider
                .Setup(p => p.GetTransactionStatusAsync(txHash, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AZOAResult<Dictionary<string, object>>
                {
                    IsError = true,
                    Message = "indeterminate"
                });
        }

        var svc = harness.CreateService();

        // This would throw MockException if ANY mutating method were called.
        var act = async () => await svc.ReconcileBridgeAsync(CancellationToken.None);
        await act.Should().NotThrowAsync(
            "a strict provider with only GetTransactionStatusAsync allowed must never throw " +
            "— the sweep is purely observational and calls no mutating provider methods");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Harness
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Test harness for the G1/G5/G6/Window-6 tests. Exposes both the
    /// <see cref="FakeBridgeStore"/> (for realistic store semantics) and a
    /// <see cref="Mock{IBridgeStore}"/> wrapper around it so tests can verify
    /// that specific store methods were/were not called. The idempotency store is
    /// similarly dual: a <see cref="FakeIdempotencyStore"/> for state and a
    /// <see cref="Mock{IIdempotencyStore}"/> that forwards to it for verification.
    ///
    /// <para>This mirrors the <c>FakeReconHarness</c> pattern from
    /// <see cref="Reconciliation.ReconciliationServiceTests"/> — same
    /// construction idioms, same <c>AssertNoOnChainMutation</c> list.</para>
    /// </summary>
    private sealed class BridgeHardeningHarness : IDisposable
    {
        private readonly FakeBridgeStore _fakeStore = new();
        private readonly FakeIdempotencyStore _fakeIdempotency = new();
        private string? _lastSeededId;

        public Mock<IBlockchainProvider> ProviderMock { get; }
        public Mock<IBlockchainProviderFactory> FactoryMock { get; } = new();
        public Mock<IBridgeStore> BridgeStoreMock { get; }
        public Mock<IIdempotencyStore> IdempotencyStoreMock { get; }

        /// <summary>Id of the most recently seeded bridge — convenience for
        /// Window-6 tests that seed one row and need its id.</summary>
        public string LastSeededId =>
            _lastSeededId ?? throw new InvalidOperationException("No bridge seeded yet.");

        public BridgeHardeningHarness(Mock<IBlockchainProvider>? providerMock = null)
        {
            ProviderMock = providerMock ?? new Mock<IBlockchainProvider>();
            ProviderMock.Setup(p => p.ChainType).Returns("Algorand");

            FactoryMock.Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
                .Returns(ProviderMock.Object);
            FactoryMock.Setup(f => f.GetDefaultProvider())
                .Returns(ProviderMock.Object);

            // Bridge store: a pass-through mock that delegates all calls to the
            // fake so tests can both verify call patterns AND observe real state.
            BridgeStoreMock = new Mock<IBridgeStore>(MockBehavior.Loose);
            BridgeStoreMock
                .Setup(s => s.GetBridgeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((id, ct) => _fakeStore.GetBridgeAsync(id, ct));
            BridgeStoreMock
                .Setup(s => s.GetNonTerminalBridgeIdsAsync(
                    It.IsAny<IReadOnlyCollection<BridgeStatus>>(),
                    It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<IReadOnlyCollection<BridgeStatus>, DateTime, int, CancellationToken>(
                    (statuses, before, batch, ct) =>
                        _fakeStore.GetNonTerminalBridgeIdsAsync(statuses, before, batch, ct));
            BridgeStoreMock
                .Setup(s => s.TryTransitionBridgeStatusAsync(
                    It.IsAny<string>(), It.IsAny<BridgeStatus>(), It.IsAny<BridgeStatus>(),
                    It.IsAny<BridgeStatusMutation?>(), It.IsAny<CancellationToken>()))
                .Returns<string, BridgeStatus, BridgeStatus, BridgeStatusMutation?, CancellationToken>(
                    (id, exp, next, mutation, ct) =>
                        _fakeStore.TryTransitionBridgeStatusAsync(id, exp, next, mutation, ct));
            BridgeStoreMock
                .Setup(s => s.GetFailedBridgesWithLockedFundsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<int, CancellationToken>((max, ct) =>
                    _fakeStore.GetFailedBridgesWithLockedFundsAsync(max, ct));
            BridgeStoreMock
                .Setup(s => s.ExistsByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((id, ct) => _fakeStore.ExistsByIdAsync(id, ct));

            // Idempotency store: same dual pattern.
            IdempotencyStoreMock = new Mock<IIdempotencyStore>(MockBehavior.Loose);
            IdempotencyStoreMock
                .Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((k, ct) => _fakeIdempotency.GetAsync(k, ct));
            IdempotencyStoreMock
                .Setup(s => s.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, CancellationToken>((k, p, ct) => _fakeIdempotency.CompleteAsync(k, p, ct));
            IdempotencyStoreMock
                .Setup(s => s.FailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, CancellationToken>((k, r, ct) => _fakeIdempotency.FailAsync(k, r, ct));
            IdempotencyStoreMock
                .Setup(s => s.TryClaimAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, CancellationToken>((k, op, ct) => _fakeIdempotency.TryClaimAsync(k, op, ct));
        }

        public ReconciliationService CreateService() =>
            new(
                BridgeStoreMock.Object,
                FactoryMock.Object,
                IdempotencyStoreMock.Object,
                Mock.Of<ILogger<ReconciliationService>>(),
                Options.Create(new ReconciliationOptions
                {
                    Enabled = true,
                    BatchSize = 100,
                    BridgeStaleAfterSeconds = 60,
                    BridgeHardStuckAfterSeconds = 900,
                    OperationStaleAfterSeconds = 60,
                    OperationHardStuckAfterSeconds = 900,
                }));

        public string SeedBridge(Action<BridgeTransactionResult> configure)
        {
            var row = new BridgeTransactionResult
            {
                Id = $"br_{Guid.NewGuid():N}",
                AvatarId = Guid.NewGuid(),
                SourceChain = "Algorand",
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
            _fakeStore.SeedBridge(row);
            _lastSeededId = row.Id;
            return row.Id;
        }

        public void SeedIdempotency(string key, IdempotencyState state, string operationType = "bridge-redeem")
            => _fakeIdempotency.Seed(key, operationType, state);

        public BridgeTransactionResult GetBridge(string id) =>
            _fakeStore.GetBridgeAsync(id).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException($"No bridge '{id}' in fake store.");

        public IdempotencyRecord? GetIdempotency(string key) =>
            _fakeIdempotency.GetAsync(key, CancellationToken.None).GetAwaiter().GetResult();

        public void SetupTxStatus(string txHash, Dictionary<string, object> status) =>
            ProviderMock
                .Setup(p => p.GetTransactionStatusAsync(txHash, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AZOAResult<Dictionary<string, object>>
                {
                    IsError = false,
                    Result = status
                });

        /// <summary>Central safety invariant: the sweep OBSERVES, never mutates on-chain.
        /// Mirrors the identical list in <c>FakeReconHarness.AssertNoOnChainMutation</c>.</summary>
        public void AssertNoOnChainMutation()
        {
            ProviderMock.Verify(p => p.MintAsync(
                It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()), Times.Never);
            ProviderMock.Verify(p => p.MintWrappedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            ProviderMock.Verify(p => p.BurnAsync(
                It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<string>(),
                It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()), Times.Never);
            ProviderMock.Verify(p => p.BurnWrappedAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            ProviderMock.Verify(p => p.TransferAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<ulong>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()), Times.Never);
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

        public void Dispose() { /* all in-memory */ }
    }
}
