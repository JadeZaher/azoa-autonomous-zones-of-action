using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Bridge;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Bridge;
using AZOA.WebAPI.Services.Reconciliation;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Services;

/// <summary>
/// B3 integrated exactly-once / replay verification sweep.
///
/// Unlike the per-decision-branch tests (BridgeRedeemRecoveryTests,
/// ReconciliationBridgeHardeningTests), this suite drives the FULL bridge flow
/// through <see cref="CrossChainBridgeService"/> AND the
/// <see cref="ReconciliationService"/> sweep over ONE shared
/// <see cref="FakeBridgeStore"/> + <see cref="FakeIdempotencyStore"/> — the
/// integrated proof that the kill switch, VAA replay ledger, avatar-scoped
/// idempotency, atomic status guards, and reconciliation compose into an
/// exactly-once system: no replayed VAA / duplicate idempotency key ever
/// double-mints or double-releases, and a crash-resume never re-broadcasts.
///
/// See Services/AGENTS.md §bridge (exactly-once invariant).
/// </summary>
public sealed class BridgeExactlyOnceReplaySweepTests
{
    private const string ValidVaa = "ZXhhY3RseS1vbmNlLXN3ZWVw"; // "exactly-once-sweep"

    // ═══════════════════════════════════════════════════════════════════════════
    // 1. Trusted flow: duplicate idempotency key never double-locks/double-mints.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Trusted_DuplicateClientKey_ExactlyOneLockAndMint()
    {
        using var h = new SweepHarness();
        var avatar = Guid.NewGuid();

        // Both calls carry the SAME client idempotency key + same avatar.
        var first = await h.Bridge().InitiateBridgeAsync(
            "Algorand", "Solana", "tok1", "recipient", avatar, amount: 1,
            mode: BridgeMode.Trusted, clientIdempotencyKey: "dup-key");
        var second = await h.Bridge().InitiateBridgeAsync(
            "Algorand", "Solana", "tok1", "recipient", avatar, amount: 1,
            mode: BridgeMode.Trusted, clientIdempotencyKey: "dup-key");

        first.IsError.Should().BeFalse();
        // The second is either an idempotent replay (success) or a rejected
        // duplicate — never a second irreversible chain effect.
        h.LockCalls.Should().Be(1, "exactly one on-chain lock across duplicate requests");
        h.MintCalls.Should().Be(1, "exactly one on-chain mint across duplicate requests");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 2. Wormhole flow: replayed VAA (same digest, different bridge) never mints.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Wormhole_ReplayedVaaAcrossBridges_ExactlyOneMint_SecondRejected()
    {
        using var h = new SweepHarness();
        var digest = WormholeAdapter.ComputeVaaDigest(ValidVaa);

        var bridgeA = h.SeedVaaReady("emitter-A", 1, digest);
        var bridgeB = h.SeedVaaReady("emitter-B", 2, digest); // SAME VAA digest, different row

        var redeemA = await h.Bridge().RedeemWithVAAAsync(bridgeA);
        var redeemB = await h.Bridge().RedeemWithVAAAsync(bridgeB);

        redeemA.IsError.Should().BeFalse("first redeem of the VAA succeeds");
        redeemB.IsError.Should().BeTrue("the SAME VAA replayed on a second bridge must be rejected");
        redeemB.Message.Should().Contain("replay");

        h.RedeemCalls.Should().Be(1, "the VAA replay ledger must permit exactly one on-chain redeem");
        h.GetBridge(bridgeA).Status.Should().Be(BridgeStatus.Completed);
        h.GetBridge(bridgeB).Status.Should().Be(BridgeStatus.Failed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 3. Crash-resume: a Redeeming row with NO consume ledger row resumes without
    //    re-broadcasting a duplicate mint, then a subsequent reconciliation sweep
    //    over the now-Completed row is a pure no-op (idempotent, zero on-chain).
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CrashResume_ThenReconcile_ExactlyOneMint_ReconcileIdempotent()
    {
        using var h = new SweepHarness(staleSeconds: 60);
        var digest = WormholeAdapter.ComputeVaaDigest(ValidVaa);

        // Crash window: VAAReady→Redeeming committed but consume + on-chain never ran.
        var bridgeId = h.SeedBridge(b =>
        {
            b.Status = BridgeStatus.Redeeming;
            b.VaaBytes = ValidVaa;
            b.VaaSignatureCount = 13;
            b.WormholeEmitterChainId = 1;
            b.WormholeEmitterAddress = "emitter-crash";
            b.WormholeSequence = 5;
            b.TargetChain = "Solana";
            b.CreatedAt = DateTime.UtcNow.AddMinutes(-10);
            b.IdempotencyKey = $"bridge-redeem:{b.Id}:{digest}";
        });
        // Stale InProgress claim left by the crashed request.
        h.Idempotency.SeedAged(
            $"bridge-redeem:{bridgeId}:{digest}", "bridge-redeem",
            IdempotencyState.InProgress, DateTime.UtcNow.AddMinutes(-5));

        // Resume: exactly one mint, ends Completed.
        var resume = await h.Bridge().RedeemWithVAAAsync(bridgeId);
        resume.IsError.Should().BeFalse("no consume row proves no prior on-chain submit — safe resume");
        h.RedeemCalls.Should().Be(1, "crash-resume must broadcast exactly once");
        h.GetBridge(bridgeId).Status.Should().Be(BridgeStatus.Completed);

        // A reconciliation sweep over the now-terminal row must be a no-op and
        // must NEVER re-broadcast (it only observes chain truth).
        var report = await h.Recon().ReconcileBridgeAsync(CancellationToken.None);
        report.Advanced.Should().Be(0);
        report.Failed.Should().Be(0);
        h.RedeemCalls.Should().Be(1, "reconciliation must not trigger a second mint");
        h.MintCalls.Should().Be(0, "wormhole redeem path uses RedeemTransferAsync, not provider MintWrapped");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 4. Kill switch honored on every value path (initiate + redeem + reverse).
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task KillSwitch_Off_RefusesRealValueOnEveryPath_ZeroOnChain()
    {
        using var h = new SweepHarness(realValueEnabled: false);
        var avatar = Guid.NewGuid();

        var init = await h.Bridge().InitiateBridgeAsync(
            "Algorand", "Solana", "tok1", "recipient", avatar, 1, BridgeMode.Trusted);
        init.IsError.Should().BeTrue("kill switch off must refuse a real-value initiate");
        init.Message.Should().Contain("disabled");

        var bridgeId = h.SeedVaaReady("emitter-kill", 1, WormholeAdapter.ComputeVaaDigest(ValidVaa));
        var redeem = await h.Bridge().RedeemWithVAAAsync(bridgeId);
        redeem.IsError.Should().BeTrue("kill switch off must refuse a real-value redeem");
        redeem.Message.Should().Contain("disabled");

        h.LockCalls.Should().Be(0);
        h.MintCalls.Should().Be(0);
        h.RedeemCalls.Should().Be(0, "no on-chain value moves while the kill switch is off");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 5. Reconciliation is idempotent: probing the same stuck row twice yields the
    //    same verdict and never a second write / re-broadcast.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reconcile_SameRowTwice_IdempotentVerdict_NoDoubleWrite()
    {
        using var h = new SweepHarness();

        // Redeeming row whose redeem tx the chain reports Confirmed → Completed once.
        var bridgeId = h.SeedBridge(b =>
        {
            b.Status = BridgeStatus.Redeeming;
            b.TargetChain = "Solana";
            b.RedemptionTxHash = "redeem_confirmed";
            b.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
        });
        h.SetupTxStatus("redeem_confirmed", new() { ["success"] = true });

        var first = await h.Recon().ReconcileBridgeAsync(CancellationToken.None);
        first.Advanced.Should().Be(1, "first sweep advances Redeeming→Completed on a confirmed redeem");
        h.GetBridge(bridgeId).Status.Should().Be(BridgeStatus.Completed);

        var second = await h.Recon().ReconcileBridgeAsync(CancellationToken.None);
        second.Advanced.Should().Be(0, "the row is already terminal — a second sweep is a no-op");
        second.Scanned.Should().Be(0, "terminal rows are excluded from the non-terminal candidate set");
        h.RedeemCalls.Should().Be(0, "reconciliation only observes; it never re-broadcasts");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Shared-store harness: one FakeBridgeStore + one FakeIdempotencyStore feed
    // BOTH the bridge service and the reconciliation sweep.
    // ═══════════════════════════════════════════════════════════════════════════

    private sealed class SweepHarness : IDisposable
    {
        public readonly FakeBridgeStore BridgeStore = new();
        public readonly FakeIdempotencyStore Idempotency = new();
        public readonly Mock<IWormholeAdapter> WormholeMock = new();
        private readonly Mock<IBlockchainProviderFactory> _factory = new();
        private readonly Mock<IBlockchainProvider> _provider = new();
        private readonly int _staleSeconds;
        private readonly bool _realValue;

        public int LockCalls;
        public int MintCalls;
        public int RedeemCalls;

        public SweepHarness(int staleSeconds = 120, bool realValueEnabled = true)
        {
            _staleSeconds = staleSeconds;
            _realValue = realValueEnabled;

            _provider.Setup(p => p.SupportsBridging).Returns(true);
            _provider.Setup(p => p.ChainType).Returns("Algorand");
            _factory.Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
                .Returns(_provider.Object);
            _factory.Setup(f => f.GetDefaultProvider()).Returns(_provider.Object);

            // Trusted-flow chain primitives count invocations (exactly-once proof).
            _provider.Setup(p => p.LockForBridgeAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    Interlocked.Increment(ref LockCalls);
                    return Task.FromResult(new AZOAResult<string> { IsError = false, Result = "lock-tx" });
                });
            _provider.Setup(p => p.MintWrappedAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    Interlocked.Increment(ref MintCalls);
                    return Task.FromResult(new AZOAResult<string> { IsError = false, Result = "mint-tx" });
                });

            // Wormhole redeem counts invocations too.
            WormholeMock.Setup(w => w.IsRouteSupported(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(true);
            WormholeMock.Setup(w => w.RedeemTransferAsync(
                    It.IsAny<string>(), It.IsAny<WormholeVAA>(),
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    Interlocked.Increment(ref RedeemCalls);
                    return Task.FromResult(new AZOAResult<WormholeRedemptionResult>
                    {
                        IsError = false,
                        Result = new WormholeRedemptionResult { TxHash = "redeem-tx", Success = true }
                    });
                });
        }

        public CrossChainBridgeService Bridge() => new(
            _factory.Object,
            WormholeMock.Object,
            Options.Create(new WormholeConfig { DefaultMode = BridgeMode.Trusted }),
            BridgeStore,
            Idempotency,
            Mock.Of<ILogger<CrossChainBridgeService>>(),
            Options.Create(new BridgeOptions
            {
                RealValueEnabled = _realValue,
                StaleClaimTakeoverSeconds = _staleSeconds
            }),
            new ConfigurationBuilder().Build());

        public ReconciliationService Recon() => new(
            BridgeStore,
            _factory.Object,
            Idempotency,
            Mock.Of<ILogger<ReconciliationService>>(),
            Options.Create(new ReconciliationOptions
            {
                Enabled = true,
                BatchSize = 100,
                BridgeStaleAfterSeconds = 1,
                BridgeHardStuckAfterSeconds = 900,
                OperationStaleAfterSeconds = 1,
                OperationHardStuckAfterSeconds = 900,
            }));

        public string SeedVaaReady(string emitterAddress, int emitterChainId, string _)
        {
            var id = $"wh_bridge_{Guid.NewGuid():N}";
            BridgeStore.SeedBridge(new BridgeTransactionResult
            {
                Id = id, AvatarId = Guid.NewGuid(),
                SourceChain = "Algorand", TargetChain = "Solana",
                SourceTokenId = "tok1", TargetAddress = "recipient",
                Amount = 1, Mode = BridgeMode.Wormhole,
                Status = BridgeStatus.VAAReady,
                VaaBytes = ValidVaa, VaaSignatureCount = 13,
                WormholeEmitterChainId = emitterChainId,
                WormholeEmitterAddress = emitterAddress,
                WormholeSequence = emitterChainId,
                CreatedAt = DateTime.UtcNow,
            });
            return id;
        }

        public string SeedBridge(Action<BridgeTransactionResult> configure)
        {
            var row = new BridgeTransactionResult
            {
                Id = $"wh_bridge_{Guid.NewGuid():N}",
                AvatarId = Guid.NewGuid(),
                SourceChain = "Algorand", TargetChain = "Solana",
                SourceTokenId = "tok1", TargetAddress = "recipient",
                Amount = 1, Mode = BridgeMode.Wormhole,
                Status = BridgeStatus.Redeeming,
                CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            };
            configure(row);
            BridgeStore.SeedBridge(row);
            return row.Id;
        }

        public BridgeTransactionResult GetBridge(string id)
            => BridgeStore.GetBridgeAsync(id).GetAwaiter().GetResult()
               ?? throw new InvalidOperationException($"Bridge '{id}' not found.");

        public void SetupTxStatus(string txHash, Dictionary<string, object> status)
            => _provider.Setup(p => p.GetTransactionStatusAsync(txHash, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AZOAResult<Dictionary<string, object>>
                {
                    IsError = false,
                    Result = status
                });

        public void Dispose() { }
    }
}
