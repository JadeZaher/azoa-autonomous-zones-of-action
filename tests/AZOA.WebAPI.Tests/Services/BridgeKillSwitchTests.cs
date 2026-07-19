using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Bridge;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Bridge;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Services;

/// <summary>
/// Phase C item 8 — kill-switch gate (<c>BridgeOptions.RealValueEnabled</c>).
/// Each test uses Moq mocks of IBridgeStore / IIdempotencyStore directly;
/// no shared fake files are required. See Services/CrossChainBridgeService.cs
/// IsSimulatedRoute + gate logic.
/// </summary>
public class BridgeKillSwitchTests
{
    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a service with the supplied flag and factory mock. Bridge/idempotency
    /// stores are strict Moq mocks — their interactions are verified per test.
    /// </summary>
    private static (CrossChainBridgeService Service,
                    Mock<IBridgeStore> BridgeStore,
                    Mock<IIdempotencyStore> IdempotencyStore)
        BuildService(bool realValueEnabled, Mock<IBlockchainProviderFactory> factoryMock)
    {
        var bridgeStore = new Mock<IBridgeStore>(MockBehavior.Strict);
        var idempotencyStore = new Mock<IIdempotencyStore>(MockBehavior.Strict);

        var svc = new CrossChainBridgeService(
            factoryMock.Object,
            Mock.Of<IWormholeAdapter>(),
            Options.Create(new WormholeConfig { DefaultMode = BridgeMode.Trusted }),
            bridgeStore.Object,
            idempotencyStore.Object,
            Mock.Of<ILogger<CrossChainBridgeService>>(),
            Options.Create(new BridgeOptions { RealValueEnabled = realValueEnabled }),
            new ConfigurationBuilder().Build(),
            new ApprovedRealValueKycGate());

        return (svc, bridgeStore, idempotencyStore);
    }

    /// <summary>Returns a factory mock where GetProvider always returns a provider with the given ChainType.</summary>
    private static Mock<IBlockchainProviderFactory> FactoryWithChainType(string chainType)
    {
        var provider = new Mock<IBlockchainProvider>();
        provider.Setup(p => p.ChainType).Returns(chainType);
        provider.Setup(p => p.SupportsBridging).Returns(true);

        var factory = new Mock<IBlockchainProviderFactory>();
        factory.Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
               .Returns(provider.Object);
        return factory;
    }

    /// <summary>Returns a factory mock where source → sourceType, target → targetType.</summary>
    private static Mock<IBlockchainProviderFactory> FactoryWithTwoChains(
        string sourceType, string targetType)
    {
        var srcProvider = new Mock<IBlockchainProvider>();
        srcProvider.Setup(p => p.ChainType).Returns(sourceType);
        srcProvider.Setup(p => p.SupportsBridging).Returns(true);

        var tgtProvider = new Mock<IBlockchainProvider>();
        tgtProvider.Setup(p => p.ChainType).Returns(targetType);
        tgtProvider.Setup(p => p.SupportsBridging).Returns(true);

        var factory = new Mock<IBlockchainProviderFactory>();
        // First call resolves source, second resolves target (IsSimulatedRoute order).
        factory.SetupSequence(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
               .Returns(srcProvider.Object)
               .Returns(tgtProvider.Object);
        return factory;
    }

    // ─── Initiate: refusal on real route ────────────────────────────────────

    /// <summary>
    /// Flag OFF + real chain (Solana) → InitiateBridgeAsync is refused before
    /// AddBridgeAsync, before any provider Lock/Mint, before any idempotency claim.
    /// The error message must name the config key so operators know what to flip.
    /// </summary>
    [Fact]
    public async Task InitiateBridge_FlagOff_RealRoute_Refused_NoPersist_NoClaim()
    {
        var factory = FactoryWithChainType("Solana");
        var (svc, bridgeStore, idempotencyStore) = BuildService(realValueEnabled: false, factory);

        var result = await svc.InitiateBridgeAsync(
            "Solana", "Algorand", "tok1", "addr", Guid.NewGuid(), 1, BridgeMode.Trusted);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("RealValueEnabled",
            "operator must know which config key unblocks the gate");

        // Pre-persist: no bridge row may be created on a refused initiate.
        bridgeStore.Verify(s => s.AddBridgeAsync(
            It.IsAny<BridgeTransactionResult>(), It.IsAny<CancellationToken>()), Times.Never);

        // No idempotency claim on refusal.
        idempotencyStore.Verify(s => s.TryClaimAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Flag OFF + both chains Simulated → gate allows the request (simulated
    /// routes are always available regardless of the kill switch).
    /// The service progresses past the gate; because the trusted flow hits
    /// TryClaimAsync we allow it here and verify we're past the guard.
    /// </summary>
    [Fact]
    public async Task InitiateBridge_FlagOff_BothSimulated_GatePassed()
    {
        var factory = FactoryWithChainType("Simulated");

        var bridgeStore = new Mock<IBridgeStore>();
        var idempotencyStore = new Mock<IIdempotencyStore>();

        // Claim wins so the flow advances beyond the gate.
        idempotencyStore.Setup(s => s.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdempotencyClaim(true, new AZOA.WebAPI.Models.Idempotency.IdempotencyRecord
            {
                Key = "k", OperationType = "bridge-trusted",
                State = AZOA.WebAPI.Models.Idempotency.IdempotencyState.InProgress,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            }));

        var svc = new CrossChainBridgeService(
            factory.Object,
            Mock.Of<IWormholeAdapter>(),
            Options.Create(new WormholeConfig { DefaultMode = BridgeMode.Trusted }),
            bridgeStore.Object,
            idempotencyStore.Object,
            Mock.Of<ILogger<CrossChainBridgeService>>(),
            Options.Create(new BridgeOptions { RealValueEnabled = false }),
            new ConfigurationBuilder().Build(),
            new ApprovedRealValueKycGate());

        var result = await svc.InitiateBridgeAsync(
            "SimChainA", "SimChainB", "tok1", "addr", Guid.NewGuid(), 1, BridgeMode.Trusted);

        // The gate did NOT refuse (no "RealValueEnabled" error).
        result.Message.Should().NotContain("RealValueEnabled",
            "a fully-simulated route must pass the kill-switch gate");

        // TryClaimAsync was called — proof we got past the gate into the flow.
        idempotencyStore.Verify(s => s.TryClaimAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Flag OFF + one chain Simulated + one chain real (Solana) → refused.
    /// BOTH chains must be Simulated for the gate to pass; one real is enough to block.
    /// </summary>
    [Fact]
    public async Task InitiateBridge_FlagOff_MixedRoute_OneSimulatedOneReal_Refused()
    {
        var factory = FactoryWithTwoChains(sourceType: "Simulated", targetType: "Solana");
        var (svc, bridgeStore, idempotencyStore) = BuildService(realValueEnabled: false, factory);

        var result = await svc.InitiateBridgeAsync(
            "SimChainA", "Solana", "tok1", "addr", Guid.NewGuid(), 1, BridgeMode.Trusted);

        result.IsError.Should().BeTrue("one real chain in the route must trigger the kill-switch");
        result.Message.Should().Contain("RealValueEnabled");

        bridgeStore.Verify(s => s.AddBridgeAsync(
            It.IsAny<BridgeTransactionResult>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Flag OFF + factory throws on GetProvider (unknown chain) → treated as real
    /// (fail-closed). The gate refuses rather than assuming the route is safe.
    /// </summary>
    [Fact]
    public async Task InitiateBridge_FlagOff_UnknownChainFactoryThrows_FailClosedRefusal()
    {
        var factory = new Mock<IBlockchainProviderFactory>();
        factory.Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
               .Throws(new InvalidOperationException("unknown chain"));

        var (svc, bridgeStore, idempotencyStore) = BuildService(realValueEnabled: false, factory);

        var result = await svc.InitiateBridgeAsync(
            "UnknownA", "UnknownB", "tok1", "addr", Guid.NewGuid(), 1, BridgeMode.Trusted);

        result.IsError.Should().BeTrue("unknown chain → cannot confirm simulated → fail-closed refusal");
        result.Message.Should().Contain("RealValueEnabled");

        bridgeStore.Verify(s => s.AddBridgeAsync(
            It.IsAny<BridgeTransactionResult>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Flag ON + real route → gate allows the request. Verified by TryClaimAsync
    /// being reached (the flow is past the guard and into the trusted path).
    /// </summary>
    [Fact]
    public async Task InitiateBridge_FlagOn_RealRoute_GatePassed()
    {
        var factory = FactoryWithChainType("Solana");

        var bridgeStore = new Mock<IBridgeStore>();
        var idempotencyStore = new Mock<IIdempotencyStore>();

        idempotencyStore.Setup(s => s.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdempotencyClaim(true, new AZOA.WebAPI.Models.Idempotency.IdempotencyRecord
            {
                Key = "k", OperationType = "bridge-trusted",
                State = AZOA.WebAPI.Models.Idempotency.IdempotencyState.InProgress,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            }));

        var wormholeConfig = new WormholeConfig { DefaultMode = BridgeMode.Trusted };
        wormholeConfig.BridgeVaults["Solana"].VaultAddress = "test-solana-bridge-vault";

        var svc = new CrossChainBridgeService(
            factory.Object,
            Mock.Of<IWormholeAdapter>(),
            Options.Create(wormholeConfig),
            bridgeStore.Object,
            idempotencyStore.Object,
            Mock.Of<ILogger<CrossChainBridgeService>>(),
            Options.Create(new BridgeOptions { RealValueEnabled = true }),
            new ConfigurationBuilder().Build(),
            new ApprovedRealValueKycGate());

        var result = await svc.InitiateBridgeAsync(
            "Solana", "Algorand", "tok1", "addr", Guid.NewGuid(), 1, BridgeMode.Trusted);

        result.Message.Should().NotContain("RealValueEnabled",
            "flag ON must allow real-route initiation");

        idempotencyStore.Verify(s => s.TryClaimAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Redeem + Reverse: refused pre-claim ────────────────────────────────

    /// <summary>
    /// Flag OFF → RedeemWithVAAAsync is refused before TryClaimAsync on a
    /// pre-existing real-route bridge (VAAReady state, non-empty VaaBytes).
    /// Same gate applies to ReverseBridgeAsync.
    /// </summary>
    [Fact]
    public async Task RedeemAndReverse_FlagOff_RealRoute_RefusedPreClaim()
    {
        var factory = FactoryWithChainType("Solana");

        // Seed a VAAReady bridge in the store mock.
        var seededBridge = new BridgeTransactionResult
        {
            Id = "wh_bridge_test",
            AvatarId = Guid.NewGuid(),
            SourceChain = "Solana",
            TargetChain = "Algorand",
            SourceTokenId = "tok1",
            TargetAddress = "recipient",
            Amount = 1,
            Mode = BridgeMode.Wormhole,
            Status = BridgeStatus.VAAReady,
            VaaBytes = "AQIDBA==",       // valid base64
            VaaSignatureCount = 13,
            WormholeEmitterChainId = 1,
            WormholeEmitterAddress = "emitter",
            WormholeSequence = 42,
            CreatedAt = DateTime.UtcNow
        };

        var completedBridge = new BridgeTransactionResult
        {
            Id = "bridge_completed",
            AvatarId = Guid.NewGuid(),
            SourceChain = "Solana",
            TargetChain = "Algorand",
            SourceTokenId = "tok1",
            TargetTokenId = "wrapped_tok",
            SourceAddress = "src",
            TargetAddress = "recipient",
            Amount = 1,
            Mode = BridgeMode.Trusted,
            Status = BridgeStatus.Completed,
            MintTxHash = "mint_tx",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            CompletedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        var idempotencyStore = new Mock<IIdempotencyStore>(MockBehavior.Strict);
        var bridgeStore = new Mock<IBridgeStore>();
        bridgeStore.Setup(s => s.GetBridgeAsync("wh_bridge_test", It.IsAny<CancellationToken>()))
                   .ReturnsAsync(seededBridge);
        bridgeStore.Setup(s => s.GetBridgeAsync("bridge_completed", It.IsAny<CancellationToken>()))
                   .ReturnsAsync(completedBridge);

        var svc = new CrossChainBridgeService(
            factory.Object,
            Mock.Of<IWormholeAdapter>(),
            Options.Create(new WormholeConfig { DefaultMode = BridgeMode.Wormhole }),
            bridgeStore.Object,
            idempotencyStore.Object,
            Mock.Of<ILogger<CrossChainBridgeService>>(),
            Options.Create(new BridgeOptions { RealValueEnabled = false }),
            new ConfigurationBuilder().Build(),
            new ApprovedRealValueKycGate());

        // Redeem must be refused before TryClaimAsync.
        var redeemResult = await svc.RedeemWithVAAAsync("wh_bridge_test");

        redeemResult.IsError.Should().BeTrue("flag OFF must block redeem on real route");
        redeemResult.Message.Should().Contain("RealValueEnabled");

        // No idempotency claim attempted (MockBehavior.Strict will catch any call).
        idempotencyStore.Verify(s => s.TryClaimAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Reverse must also be refused (uses the same gate).
        var reverseResult = await svc.ReverseBridgeAsync("bridge_completed", "refund_addr");

        reverseResult.IsError.Should().BeTrue("flag OFF must block reverse on real route");
        reverseResult.Message.Should().Contain("RealValueEnabled");

        idempotencyStore.Verify(s => s.TryClaimAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── GetSupportedRoutesAsync: RealValueEnabled per-route ────────────────

    /// <summary>
    /// GetSupportedRoutesAsync sets per-route RealValueEnabled correctly:
    ///   flag OFF → real routes show false, simulated route shows true.
    ///   flag ON  → all routes show true.
    /// </summary>
    [Fact]
    public async Task GetSupportedRoutes_FlagOffAndOn_PerRouteRealValueEnabledCorrect()
    {
        // Build providers: Solana (real), Simulated (sim).
        var solanaProvider = new Mock<IBlockchainProvider>();
        solanaProvider.Setup(p => p.ChainType).Returns("Solana");
        solanaProvider.Setup(p => p.SupportsBridging).Returns(true);

        var simProvider = new Mock<IBlockchainProvider>();
        simProvider.Setup(p => p.ChainType).Returns("Simulated");
        simProvider.Setup(p => p.SupportsBridging).Returns(true);

        // Factory returns Solana for "Solana", Simulated for "Simulated" — used by
        // both GetAllEnabledProviders (route enumeration) and IsSimulatedRoute.
        var factory = new Mock<IBlockchainProviderFactory>();
        factory.Setup(f => f.GetAllEnabledProviders())
               .Returns(new[] { solanaProvider.Object, simProvider.Object });
        factory.Setup(f => f.GetProvider("Solana", It.IsAny<ChainNetwork>()))
               .Returns(solanaProvider.Object);
        factory.Setup(f => f.GetProvider("Simulated", It.IsAny<ChainNetwork>()))
               .Returns(simProvider.Object);

        var wormhole = new Mock<IWormholeAdapter>();
        wormhole.Setup(w => w.IsRouteSupported(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
        wormhole.Setup(w => w.GetWormholeChainId(It.IsAny<string>())).Returns((int?)null);

        // ── Flag OFF ──
        var svcOff = new CrossChainBridgeService(
            factory.Object, wormhole.Object,
            Options.Create(new WormholeConfig { DefaultMode = BridgeMode.Trusted }),
            new Mock<IBridgeStore>().Object,
            new Mock<IIdempotencyStore>().Object,
            Mock.Of<ILogger<CrossChainBridgeService>>(),
            Options.Create(new BridgeOptions { RealValueEnabled = false }),
            new ConfigurationBuilder().Build(),
            new ApprovedRealValueKycGate());

        var routesOff = (await svcOff.GetSupportedRoutesAsync()).Result!.ToList();

        // Solana→Simulated and Simulated→Solana are mixed routes (real), should be false.
        // Simulated→Simulated is a sim route, should be true.
        var simToSim = routesOff.SingleOrDefault(r => r.SourceChain == "Simulated" && r.TargetChain == "Simulated");
        simToSim.Should().NotBeNull("Simulated→Simulated route must exist");
        simToSim!.RealValueEnabled.Should().BeTrue(
            "simulated route is always available — RealValueEnabled=true even when flag is off");

        var realRoutes = routesOff.Where(r =>
            r.SourceChain != "Simulated" || r.TargetChain != "Simulated").ToList();
        realRoutes.Should().OnlyContain(r => r.RealValueEnabled == false,
            "flag OFF → non-simulated routes must show RealValueEnabled=false");

        // ── Flag ON ──
        var svcOn = new CrossChainBridgeService(
            factory.Object, wormhole.Object,
            Options.Create(new WormholeConfig { DefaultMode = BridgeMode.Trusted }),
            new Mock<IBridgeStore>().Object,
            new Mock<IIdempotencyStore>().Object,
            Mock.Of<ILogger<CrossChainBridgeService>>(),
            Options.Create(new BridgeOptions { RealValueEnabled = true }),
            new ConfigurationBuilder().Build(),
            new ApprovedRealValueKycGate());

        var routesOn = (await svcOn.GetSupportedRoutesAsync()).Result!.ToList();

        routesOn.Should().OnlyContain(r => r.RealValueEnabled == true,
            "flag ON → every route (real or sim) must show RealValueEnabled=true");
    }
}
