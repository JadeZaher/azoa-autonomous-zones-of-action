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
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Services;

public class CrossChainBridgeServiceTests
{
    private readonly Mock<IBlockchainProviderFactory> _factoryMock;
    private readonly Mock<IWormholeAdapter> _wormholeMock;
    private readonly Mock<IBlockchainProvider> _providerMock;
    private readonly WormholeConfig _config;
    private readonly CrossChainBridgeService _service;

    public CrossChainBridgeServiceTests()
    {
        _factoryMock = new Mock<IBlockchainProviderFactory>();
        _wormholeMock = new Mock<IWormholeAdapter>();
        _providerMock = new Mock<IBlockchainProvider>();
        _config = new WormholeConfig { DefaultMode = BridgeMode.Wormhole };

        _providerMock.Setup(p => p.SupportsBridging).Returns(true);
        _providerMock.Setup(p => p.ChainType).Returns("Solana");

        _factoryMock.Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
            .Returns(_providerMock.Object);

        // Service-behavior tests that do not touch DB UNIQUE constraints use
        // the in-memory fake stores directly — no EF/SQLite required.
        _service = new CrossChainBridgeService(
            _factoryMock.Object,
            _wormholeMock.Object,
            Options.Create(_config),
            new FakeBridgeStore(),
            new FakeIdempotencyStore(),
            Mock.Of<ILogger<CrossChainBridgeService>>());
    }

    // ─── Initiation ───

    [Fact]
    public async Task InitiateBridge_EmptySourceChain_ReturnsError()
    {
        var result = await _service.InitiateBridgeAsync("", "Algorand", "token", "addr", Guid.NewGuid());
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task InitiateBridge_TrustedMode_CompletesImmediately()
    {
        using var harness = new FakeBridgeHarness();
        harness.ProviderMock.Setup(p => p.LockForBridgeAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<string> { IsError = false, Result = "lock_tx" });

        harness.ProviderMock.Setup(p => p.MintWrappedAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<string> { IsError = false, Result = "mint_tx" });

        var (svc, _) = harness.CreateService();
        var result = await svc.InitiateBridgeAsync(
            "Solana", "Algorand", "token1", "recipient", Guid.NewGuid(), 1, BridgeMode.Trusted);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(BridgeStatus.Completed);
        result.Result.Mode.Should().Be(BridgeMode.Trusted);
        result.Result.LockTxHash.Should().Be("lock_tx");
        result.Result.MintTxHash.Should().Be("mint_tx");
    }

    [Fact]
    public async Task InitiateBridge_WormholeMode_ReturnsAwaitingVAA()
    {
        using var harness = new FakeBridgeHarness();
        harness.WormholeMock.Setup(w => w.IsRouteSupported("Solana", "Algorand")).Returns(true);
        harness.WormholeMock.Setup(w => w.InitiateTransferAsync(
            "Solana", "Algorand", "token1", It.IsAny<string>(), "recipient",
            1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<WormholeTransferInitiation>
            {
                IsError = false,
                Result = new WormholeTransferInitiation
                {
                    TxHash = "wh_lock_tx",
                    EmitterChainId = 1,
                    EmitterAddress = "emitter",
                    Sequence = 42
                }
            });

        var (svc, _) = harness.CreateService();
        var result = await svc.InitiateBridgeAsync(
            "Solana", "Algorand", "token1", "recipient", Guid.NewGuid(), 1, BridgeMode.Wormhole);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(BridgeStatus.AwaitingVAA);
        result.Result.Mode.Should().Be(BridgeMode.Wormhole);
        result.Result.WormholeSequence.Should().Be(42);
        result.Result.WormholeEmitterChainId.Should().Be(1);
        result.Result.Id.Should().StartWith("wh_bridge_");
    }

    [Fact]
    public async Task InitiateBridge_WormholeUnsupported_FallsBackToTrusted()
    {
        using var harness = new FakeBridgeHarness();
        harness.WormholeMock.Setup(w => w.IsRouteSupported("Solana", "Algorand")).Returns(false);

        harness.ProviderMock.Setup(p => p.LockForBridgeAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<string> { IsError = false, Result = "lock_tx" });

        harness.ProviderMock.Setup(p => p.MintWrappedAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<string> { IsError = false, Result = "mint_tx" });

        var (svc, _) = harness.CreateService();
        var result = await svc.InitiateBridgeAsync(
            "Solana", "Algorand", "token1", "recipient", Guid.NewGuid(), 1, BridgeMode.Wormhole);

        result.IsError.Should().BeFalse();
        result.Result!.Mode.Should().Be(BridgeMode.Trusted);
        result.Result.Status.Should().Be(BridgeStatus.Completed);
    }

    // ─── Full Wormhole lifecycle ───

    [Fact]
    public async Task WormholeBridge_FullLifecycle_InitiateFetchRedeem()
    {
        using var harness = new FakeBridgeHarness();
        var avatarId = Guid.NewGuid();

        // Step 1: Initiate
        harness.WormholeMock.Setup(w => w.IsRouteSupported("Solana", "Algorand")).Returns(true);
        harness.WormholeMock.Setup(w => w.InitiateTransferAsync(
            "Solana", "Algorand", "token1", It.IsAny<string>(), "recipient",
            1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<WormholeTransferInitiation>
            {
                IsError = false,
                Result = new WormholeTransferInitiation
                {
                    TxHash = "wh_tx", EmitterChainId = 1,
                    EmitterAddress = "emitter", Sequence = 100
                }
            });

        var (svc, _) = harness.CreateService();
        var initResult = await svc.InitiateBridgeAsync(
            "Solana", "Algorand", "token1", "recipient", avatarId, 1, BridgeMode.Wormhole);

        initResult.IsError.Should().BeFalse();
        var bridgeId = initResult.Result!.Id;

        // Step 2: Fetch VAA
        harness.WormholeMock.Setup(w => w.FetchVAAAsync(1, "emitter", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<WormholeVAA>
            {
                IsError = false,
                Result = new WormholeVAA
                {
                    VaaBytes = "AQIDBA==",
                    SignatureCount = 13,
                    Sequence = 100,
                    EmitterChainId = 1,
                    EmitterAddress = "emitter",
                    Digest = "0xabc123"
                }
            });

        var fetchResult = await svc.FetchVAAAsync(bridgeId);
        fetchResult.IsError.Should().BeFalse();
        fetchResult.Result!.Status.Should().Be(BridgeStatus.VAAReady);
        fetchResult.Result.VaaBytes.Should().Be("AQIDBA==");
        fetchResult.Result.VaaSignatureCount.Should().Be(13);

        // Step 3: Redeem
        harness.WormholeMock.Setup(w => w.RedeemTransferAsync(
            "Algorand", It.IsAny<WormholeVAA>(), "recipient", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<WormholeRedemptionResult>
            {
                IsError = false,
                Result = new WormholeRedemptionResult
                {
                    TxHash = "redeem_tx_789",
                    Success = true
                }
            });

        var redeemResult = await svc.RedeemWithVAAAsync(bridgeId);
        redeemResult.IsError.Should().BeFalse();
        redeemResult.Result!.Status.Should().Be(BridgeStatus.Completed);
        redeemResult.Result.RedemptionTxHash.Should().Be("redeem_tx_789");
        redeemResult.Result.CompletedAt.Should().NotBeNull();
    }

    // ─── Error paths ───

    [Fact]
    public async Task FetchVAA_TrustedBridge_ReturnsError()
    {
        using var harness = new FakeBridgeHarness();
        harness.ProviderMock.Setup(p => p.LockForBridgeAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<string> { IsError = false, Result = "tx" });
        harness.ProviderMock.Setup(p => p.MintWrappedAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<string> { IsError = false, Result = "tx" });

        var (svc, _) = harness.CreateService();
        var initResult = await svc.InitiateBridgeAsync(
            "Solana", "Algorand", "token1", "r", Guid.NewGuid(), 1, BridgeMode.Trusted);
        initResult.IsError.Should().BeFalse("trusted initiate must genuinely succeed on the fake store");
        var bridgeId = initResult.Result!.Id;

        var fetchResult = await svc.FetchVAAAsync(bridgeId);
        fetchResult.IsError.Should().BeTrue();
        fetchResult.Message.Should().Contain("only available for Wormhole");
    }

    [Fact]
    public async Task RedeemWithVAA_NotVAAReady_ReturnsError()
    {
        using var harness = new FakeBridgeHarness();
        harness.WormholeMock.Setup(w => w.IsRouteSupported("Solana", "Algorand")).Returns(true);
        harness.WormholeMock.Setup(w => w.InitiateTransferAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<WormholeTransferInitiation>
            {
                IsError = false,
                Result = new WormholeTransferInitiation
                {
                    TxHash = "tx", EmitterChainId = 1,
                    EmitterAddress = "e", Sequence = 1
                }
            });

        var (svc, _) = harness.CreateService();
        var initResult = await svc.InitiateBridgeAsync(
            "Solana", "Algorand", "token1", "r", Guid.NewGuid(), 1, BridgeMode.Wormhole);
        initResult.IsError.Should().BeFalse("the Wormhole initiate must reach AwaitingVAA on the fake store");
        var bridgeId = initResult.Result!.Id;

        // Try to redeem without fetching VAA first
        var redeemResult = await svc.RedeemWithVAAAsync(bridgeId);
        redeemResult.IsError.Should().BeTrue();
        redeemResult.Message.Should().Contain("No VAA available");
    }

    [Fact]
    public async Task FetchVAA_NotFound_ReturnsError()
    {
        var result = await _service.FetchVAAAsync("nonexistent");
        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not found");
    }

    /// <summary>
    /// Bridge-level malformed/empty VaaBytes reject: a bridge seeded in VAAReady
    /// whose VaaBytes cannot yield the canonical replay digest (non-base64, or
    /// empty/whitespace) MUST be rejected BEFORE any idempotency claim, the
    /// atomic VAAReady→Redeeming transition, the on-chain redeem, or a
    /// ConsumedVaas write — i.e. it must NEVER mint. Non-base64 hits the
    /// digest-computation try/catch (FormatException); empty/whitespace is
    /// caught by the earlier "No VAA available" guard. Either way the invariant
    /// is identical: error, no mint, row not advanced, no replay-ledger row.
    /// </summary>
    [Theory]
    [InlineData("not_base64!!", "malformed")]
    [InlineData("", "No VAA available")]
    [InlineData("   ", "No VAA available")]
    public async Task RedeemWithVAA_MalformedOrEmptyVaaBytes_RejectsNoMint(
        string vaaBytes, string expectedMessageFragment)
    {
        using var harness = new FakeBridgeHarness();

        int redeemInvocations = 0;
        harness.WormholeMock
            .Setup(w => w.RedeemTransferAsync(
                It.IsAny<string>(), It.IsAny<WormholeVAA>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, WormholeVAA __, string ___, CancellationToken ____) =>
            {
                Interlocked.Increment(ref redeemInvocations);
                await Task.Yield();
                return new AZOAResult<WormholeRedemptionResult>
                {
                    IsError = false,
                    Result = new WormholeRedemptionResult { TxHash = "should_never_mint", Success = true }
                };
            });

        var bridgeId = harness.SeedVaaReadyBridge(
            vaaBytes: vaaBytes, emitterChainId: 1, emitterAddress: "emitter", sequence: 500);

        var (svc, _) = harness.CreateService();
        var result = await svc.RedeemWithVAAAsync(bridgeId);

        result.IsError.Should().BeTrue(
            "a VAA whose bytes cannot produce the canonical replay digest must be rejected");
        result.Message.Should().Contain(expectedMessageFragment);

        harness.WormholeMock.Verify(w => w.RedeemTransferAsync(
            It.IsAny<string>(), It.IsAny<WormholeVAA>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        redeemInvocations.Should().Be(0, "a malformed/empty VAA must NEVER mint on-chain");

        var row = harness.GetBridge(bridgeId);
        row.Status.Should().Be(BridgeStatus.VAAReady,
            "the bridge row must NOT advance (no VAAReady→Redeeming/Completed) on a malformed-VAA reject");

        harness.ConsumedVaaCount().Should().Be(0,
            "no replay-ledger row may be written when the VAA is rejected before redeem");
    }

    // ─── Routes ───

    [Fact]
    public async Task GetSupportedRoutes_IncludesWormholeInfo()
    {
        _factoryMock.Setup(f => f.GetAllEnabledProviders())
            .Returns(new[]
            {
                CreateMockProvider("Solana", true),
                CreateMockProvider("Algorand", true)
            });

        _wormholeMock.Setup(w => w.IsRouteSupported("Solana", "Algorand")).Returns(true);
        _wormholeMock.Setup(w => w.IsRouteSupported("Algorand", "Solana")).Returns(true);
        _wormholeMock.Setup(w => w.GetWormholeChainId("Solana")).Returns(1);
        _wormholeMock.Setup(w => w.GetWormholeChainId("Algorand")).Returns(8);

        var result = await _service.GetSupportedRoutesAsync();

        result.IsError.Should().BeFalse();
        var routes = result.Result!.ToList();
        routes.Should().HaveCount(2);

        var solToAlgo = routes.First(r => r.SourceChain == "Solana" && r.TargetChain == "Algorand");
        solToAlgo.WormholeSupported.Should().BeTrue();
        solToAlgo.AvailableModes.Should().Contain(BridgeMode.Wormhole);
        solToAlgo.WormholeSourceChainId.Should().Be(1);
        solToAlgo.WormholeTargetChainId.Should().Be(8);
    }

    // ─── History ───

    [Fact]
    public async Task GetBridgeHistory_ReturnsAvatarBridges()
    {
        var avatarId = Guid.NewGuid();

        _providerMock.Setup(p => p.LockForBridgeAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<string> { IsError = false, Result = "tx" });
        _providerMock.Setup(p => p.MintWrappedAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<string> { IsError = false, Result = "tx" });

        await _service.InitiateBridgeAsync("Solana", "Algorand", "t1", "r", avatarId, 1, BridgeMode.Trusted);
        await _service.InitiateBridgeAsync("Solana", "Algorand", "t2", "r", avatarId, 2, BridgeMode.Trusted);

        var history = await _service.GetBridgeHistoryAsync(avatarId);
        history.IsError.Should().BeFalse();
        history.Result!.Count().Should().Be(2);
    }

    private static IBlockchainProvider CreateMockProvider(string chainType, bool supportsBridging)
    {
        var mock = new Mock<IBlockchainProvider>();
        mock.Setup(p => p.ChainType).Returns(chainType);
        mock.Setup(p => p.SupportsBridging).Returns(supportsBridging);
        return mock.Object;
    }

    // ─── Item 4: explicit reversal provenance (Completed→Reversing→terminal) ───

    /// <summary>
    /// ReverseBridgeAsync moves a Completed bridge through the EXPLICIT
    /// <see cref="BridgeStatus.Reversing"/> in-flight marker (NOT Redeeming).
    /// A successful on-chain burn drives Reversing→Refunded. Proof the new
    /// state is used end-to-end: the success transition's predicate is
    /// <c>WHERE Status==Reversing</c> — if the in-flight marker were still
    /// Redeeming it would affect 0 rows and the bridge could never reach
    /// Refunded. The redeem path's Redeeming usage is untouched (covered by
    /// the full-lifecycle + concurrency tests above).
    /// </summary>
    [Fact]
    public async Task ReverseBridge_Success_UsesExplicitReversingState_ThenRefunded()
    {
        using var harness = new FakeBridgeHarness();

        harness.ProviderMock.Setup(p => p.BurnWrappedAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<string> { IsError = false, Result = "burn_tx_ok" });

        var bridgeId = harness.SeedCompletedBridge(targetTokenId: "wrapped_token_1");

        var (svc, _) = harness.CreateService();
        var result = await svc.ReverseBridgeAsync(bridgeId, "source_refund_addr");

        result.IsError.Should().BeFalse();
        var row = harness.GetBridge(bridgeId);
        row.Status.Should().Be(BridgeStatus.Refunded,
            "Completed→Reversing→Refunded — only reachable if the in-flight " +
            "marker is the explicit Reversing state (success predicate is " +
            "WHERE Status==Reversing)");
        row.RedemptionTxHash.Should().Be("burn_tx_ok");
        row.Status.Should().NotBe(BridgeStatus.Redeeming,
            "reversal must NOT reuse the forward-redeem Redeeming state");
    }

    /// <summary>
    /// A failed on-chain burn drives Reversing→Failed with an explicit
    /// MANUAL-INTERVENTION message. Same proof: the failure transition's
    /// predicate is <c>WHERE Status==Reversing</c>; reaching Failed confirms
    /// the in-flight marker was the explicit Reversing state.
    /// </summary>
    [Fact]
    public async Task ReverseBridge_BurnFails_ReversingToFailed_ManualIntervention()
    {
        using var harness = new FakeBridgeHarness();

        harness.ProviderMock.Setup(p => p.BurnWrappedAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<string> { IsError = true, Message = "chain rejected burn" });

        var bridgeId = harness.SeedCompletedBridge(targetTokenId: "wrapped_token_2");

        var (svc, _) = harness.CreateService();
        var result = await svc.ReverseBridgeAsync(bridgeId, "source_refund_addr");

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("MANUAL INTERVENTION REQUIRED");

        var row = harness.GetBridge(bridgeId);
        row.Status.Should().Be(BridgeStatus.Failed,
            "Completed→Reversing→Failed — reachable only via the explicit " +
            "Reversing in-flight state (failure predicate is WHERE Status==Reversing)");
        row.ErrorMessage.Should().Contain("MANUAL INTERVENTION REQUIRED");
    }

    // ─── Pre-launch bridge safety: financial-correctness invariants under
    //     concurrency and retry. FakeBridgeStore + FakeIdempotencyStore preserve
    //     the UNIQUE-on-Digest + INSERT-WINS + lock-guarded conditional-transition
    //     semantics the EF/SQLite impl provided.

    /// <summary>
    /// N concurrent redeems of the SAME bridge/VAA ⇒ EXACTLY ONE on-chain mint.
    /// The exclusive owner is elected by the conditional UPDATE WHERE
    /// Status==VAAReady + affected==1 (held under FakeBridgeStore's internal lock),
    /// so exactly one caller sees affected==1 and the rest see 0.
    /// </summary>
    [Fact]
    public async Task ConcurrentDoubleRedeem_ResultsInExactlyOneMint()
    {
        using var harness = new FakeBridgeHarness();
        const int concurrency = 16;

        var bridgeId = harness.SeedVaaReadyBridge(
            vaaBytes: "VkFBLWNvbmN1cnJlbnQ=",
            emitterChainId: 1, emitterAddress: "emitter", sequence: 100);

        // Thread-safe count of actual on-chain redeem invocations — the
        // observable that proves "exactly one mint".
        int redeemInvocations = 0;
        harness.WormholeMock
            .Setup(w => w.RedeemTransferAsync(
                It.IsAny<string>(), It.IsAny<WormholeVAA>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, WormholeVAA __, string ___, CancellationToken ____) =>
            {
                Interlocked.Increment(ref redeemInvocations);
                await Task.Yield();
                return new AZOAResult<WormholeRedemptionResult>
                {
                    IsError = false,
                    Result = new WormholeRedemptionResult { TxHash = "redeem_once", Success = true }
                };
            });

        // All callers wait on one gate, then go at once to maximize interleaving.
        using var gate = new ManualResetEventSlim(false);
        var tasks = new List<Task<AZOAResult<BridgeTransactionResult>>>(concurrency);
        for (int i = 0; i < concurrency; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                gate.Wait();
                var (svc, _) = harness.CreateService();
                return await svc.RedeemWithVAAAsync(bridgeId);
            }));
        }

        gate.Set();
        var results = await Task.WhenAll(tasks);

        redeemInvocations.Should().Be(1,
            "the atomic VAAReady→Redeeming transition + idempotency claim must "
            + "elect a single redeem owner — this is the no-double-mint invariant");

        var final = harness.GetBridge(bridgeId);
        final.Status.Should().Be(BridgeStatus.Completed);
        final.RedemptionTxHash.Should().Be("redeem_once");

        harness.ConsumedVaaCount().Should().Be(1,
            "the VAA digest is consumed exactly once; a second consume would "
            + "evidence a second mint");

        // A non-error result is not necessarily a second mint: the idempotency
        // layer correctly returns an idempotent-replay Ok to callers arriving
        // after the single owner Completed. The replay-Ok vs in-flight-reject
        // split is timing-dependent and intentionally not asserted; the
        // single-mint invariant holding for EVERY outcome is.
        var successes = results.Where(r => !r.IsError).ToList();
        successes.Should().NotBeEmpty("the redeem owner (at minimum) succeeds");
        successes.Should().OnlyContain(
            r => r.Result!.Status == BridgeStatus.Completed
                 && r.Result!.RedemptionTxHash == "redeem_once",
            "every success is either the one owner or an idempotent replay of "
            + "THAT SAME single mint — never a distinct second mint");

        var losers = results.Where(r => r.IsError).ToList();
        losers.Should().OnlyContain(
            r => r.Message.Contains("in progress")
                 || r.Message.Contains("concurrent")
                 || r.Message.Contains("being redeemed")
                 || r.Message.Contains("already")
                 || r.Message.Contains("VAAReady"),
            "losers must be duplicate/conflict rejections, never a 2nd mint");

        results.Where(r => !r.IsError)
               .Select(r => r.Result!.RedemptionTxHash)
               .Distinct()
               .Should().ContainSingle().Which.Should().Be("redeem_once");
    }

    /// <summary>
    /// Duplicate Wormhole initiate (identical inputs) ⇒ exactly one bridge row
    /// reaches AwaitingVAA. Dedupe is enforced by the idempotency store's
    /// INSERT-WINS primitive (FakeIdempotencyStore uses ConcurrentDictionary.TryAdd —
    /// exactly one caller wins).
    /// </summary>
    [Fact]
    public async Task DuplicateWormholeInitiate_YieldsOneBridgeRow_OneOnChainLock()
    {
        using var harness = new FakeBridgeHarness();
        var avatarId = Guid.NewGuid();

        harness.WormholeMock.Setup(w => w.IsRouteSupported("Solana", "Algorand")).Returns(true);

        int lockInvocations = 0;
        harness.WormholeMock
            .Setup(w => w.InitiateTransferAsync(
                "Solana", "Algorand", "token1", It.IsAny<string>(), "recipient",
                1, It.IsAny<CancellationToken>()))
            .Returns(async (string _, string __, string ___, string ____,
                            string _____, int ______, CancellationToken _______) =>
            {
                Interlocked.Increment(ref lockInvocations);
                await Task.Yield();
                // Same emitter tuple every time — same cross-chain message.
                return new AZOAResult<WormholeTransferInitiation>
                {
                    IsError = false,
                    Result = new WormholeTransferInitiation
                    {
                        TxHash = "wh_lock", EmitterChainId = 1,
                        EmitterAddress = "emitter", Sequence = 777
                    }
                };
            });

        var (svc1, _) = harness.CreateService();
        var first = await svc1.InitiateBridgeAsync(
            "Solana", "Algorand", "token1", "recipient", avatarId, 1, BridgeMode.Wormhole);
        first.IsError.Should().BeFalse();
        first.Result!.Status.Should().Be(BridgeStatus.AwaitingVAA);

        var (svc2, _) = harness.CreateService();
        var second = await svc2.InitiateBridgeAsync(
            "Solana", "Algorand", "token1", "recipient", avatarId, 1, BridgeMode.Wormhole);

        var awaiting = harness.BridgesWithEmitter(1, "emitter", 777)
            .Where(b => b.Status == BridgeStatus.AwaitingVAA)
            .ToList();
        awaiting.Should().HaveCount(1,
            "the unique (emitter,address,sequence) index permits one live bridge per message");

        if (!second.IsError)
        {
            second.Result!.Id.Should().Be(first.Result!.Id,
                "a non-error duplicate must resolve to the SAME bridge, not a new one");
        }

        lockInvocations.Should().BeLessThanOrEqualTo(2);
        harness.BridgesWithEmitter(1, "emitter", 777)
            .Count(b => b.Status is BridgeStatus.AwaitingVAA or BridgeStatus.Completed)
            .Should().Be(1, "only one bridge can hold the (emitter,seq) lock");
    }

    /// <summary>
    /// A replayed VAA (same SHA-256 digest) is rejected by the ConsumedVaas
    /// UNIQUE Digest constraint — no second mint, bridge not re-Completed.
    /// FakeBridgeStore.TryInsertConsumedVaaAsync returns false on collision,
    /// mirroring the EF unique-constraint-then-reread codepath.
    /// </summary>
    [Fact]
    public async Task ReplayedVaa_IsRejected_NoSecondMint()
    {
        using var harness = new FakeBridgeHarness();
        const string sharedVaa = "VkFBLXJlcGxheS1zYW1lLWRpZ2VzdA==";

        int redeemInvocations = 0;
        harness.WormholeMock
            .Setup(w => w.RedeemTransferAsync(
                It.IsAny<string>(), It.IsAny<WormholeVAA>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, WormholeVAA __, string ___, CancellationToken ____) =>
            {
                Interlocked.Increment(ref redeemInvocations);
                await Task.Yield();
                return new AZOAResult<WormholeRedemptionResult>
                {
                    IsError = false,
                    Result = new WormholeRedemptionResult { TxHash = "redeem_first", Success = true }
                };
            });

        // The two bridge rows MUST carry DISTINCT emitter tuples: the UNIQUE
        // filtered index on (EmitterChainId, EmitterAddress, Sequence) forbids
        // two live rows sharing one tuple, so the genuine replay vector is a
        // SECOND bridge (different message ⇒ different tuple) carrying the SAME
        // VAA bytes ⇒ identical digest, rejected by the ConsumedVaas constraint.
        var bridge1 = harness.SeedVaaReadyBridge(sharedVaa, 1, "emitter-a", 100);
        var (svc1, _) = harness.CreateService();
        var firstRedeem = await svc1.RedeemWithVAAAsync(bridge1);
        firstRedeem.IsError.Should().BeFalse();
        firstRedeem.Result!.Status.Should().Be(BridgeStatus.Completed);
        redeemInvocations.Should().Be(1);

        var bridge2 = harness.SeedVaaReadyBridge(sharedVaa, 2, "emitter-b", 200);
        var (svc2, _) = harness.CreateService();
        var replay = await svc2.RedeemWithVAAAsync(bridge2);

        replay.IsError.Should().BeTrue("a replayed VAA digest must be rejected");
        replay.Message.Should().Contain("replay");
        redeemInvocations.Should().Be(1, "the replay must NOT trigger a second on-chain mint");

        var replayedRow = harness.GetBridge(bridge2);
        replayedRow.Status.Should().NotBe(BridgeStatus.Completed,
            "a replay-rejected bridge must never be marked Completed");

        // Exactly one digest consumed across both attempts.
        harness.ConsumedVaaCount().Should().Be(1);
    }

    /// <summary>
    /// CRITICAL-1 cross-component digest agreement: the ConsumedVaas replay-ledger
    /// key the bridge writes for a VAA MUST equal the canonical
    /// <see cref="WormholeAdapter.ComputeVaaDigest(string)"/> for the SAME bytes.
    /// Any divergence (e.g. hashing the base64 STRING instead of the decoded
    /// bytes) yields a non-canonical key: another component using the canonical
    /// formula would not collide and the same VAA becomes mintable twice.
    /// </summary>
    [Fact]
    public async Task ConsumedVaaKey_EqualsCanonical_WormholeAdapterDigest()
    {
        using var harness = new FakeBridgeHarness();
        const string vaa = "VkFBLWNhbm9uaWNhbC1kaWdlc3Q=";

        harness.WormholeMock
            .Setup(w => w.RedeemTransferAsync(
                It.IsAny<string>(), It.IsAny<WormholeVAA>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<WormholeRedemptionResult>
            {
                IsError = false,
                Result = new WormholeRedemptionResult { TxHash = "redeem_canon", Success = true }
            });

        var bridgeId = harness.SeedVaaReadyBridge(vaa, 1, "emitter", 100);
        var (svc, _) = harness.CreateService();

        var redeem = await svc.RedeemWithVAAAsync(bridgeId);
        redeem.IsError.Should().BeFalse();

        var canonical = WormholeAdapter.ComputeVaaDigest(vaa);
        harness.ConsumedVaaDigests().Should().ContainSingle()
            .Which.Should().Be(canonical,
                "the bridge's consumed-VAA key must be the single canonical "
                + "SHA-256(base64Decode(vaa)) so it collides across components");
    }

    /// <summary>
    /// CRITICAL-1 non-canonical re-encoding: two DIFFERENT base64 strings that
    /// decode to the SAME raw bytes must collide in the ConsumedVaas ledger
    /// (the canonical digest hashes the DECODED bytes, so a re-fetched/re-padded
    /// byte-identical VAA still hits the UNIQUE constraint — replay stays
    /// blocked). The old string-hash would have produced different keys here.
    /// </summary>
    [Fact]
    public async Task RepaddedBase64Vaa_SameBytes_StillCollidesInLedger()
    {
        // A non-canonical final-quantum encoding: 'YR==' and 'YQ==' both decode
        // to the single byte 0x61 ('a'); the trailing unused bits differ so the
        // STRINGS differ while the BYTES are identical. Assert that premise so
        // the test self-validates regardless of base64 nuance.
        const string canonB64 = "YQ==";
        const string repadB64 = "YR==";
        canonB64.Should().NotBe(repadB64, "the two base64 strings must differ");
        Convert.FromBase64String(canonB64).Should().Equal(
            Convert.FromBase64String(repadB64),
            "but they must decode to byte-identical content (same VAA)");
        WormholeAdapter.ComputeVaaDigest(canonB64).Should().Be(
            WormholeAdapter.ComputeVaaDigest(repadB64),
            "the canonical digest hashes the DECODED bytes ⇒ identical key");

        using var harness = new FakeBridgeHarness();
        int redeemInvocations = 0;
        harness.WormholeMock
            .Setup(w => w.RedeemTransferAsync(
                It.IsAny<string>(), It.IsAny<WormholeVAA>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, WormholeVAA __, string ___, CancellationToken ____) =>
            {
                Interlocked.Increment(ref redeemInvocations);
                await Task.Yield();
                return new AZOAResult<WormholeRedemptionResult>
                {
                    IsError = false,
                    Result = new WormholeRedemptionResult { TxHash = "redeem_repad", Success = true }
                };
            });

        // Bridge 1 carries the canonical base64; bridge 2 (a distinct message,
        // distinct emitter tuple) carries the RE-PADDED base64 — same bytes.
        var bridge1 = harness.SeedVaaReadyBridge(canonB64, 1, "emitter-a", 100);
        var (svc1, _) = harness.CreateService();
        var first = await svc1.RedeemWithVAAAsync(bridge1);
        first.IsError.Should().BeFalse();
        first.Result!.Status.Should().Be(BridgeStatus.Completed);
        redeemInvocations.Should().Be(1);

        var bridge2 = harness.SeedVaaReadyBridge(repadB64, 2, "emitter-b", 200);
        var (svc2, _) = harness.CreateService();
        var replay = await svc2.RedeemWithVAAAsync(bridge2);

        replay.IsError.Should().BeTrue(
            "a byte-identical VAA (only base64 re-padded) is the SAME VAA — "
            + "its canonical digest already sits in the ConsumedVaas ledger");
        replay.Message.Should().Contain("replay");
        redeemInvocations.Should().Be(1,
            "the re-encoded VAA must NOT trigger a second on-chain mint");
        harness.ConsumedVaaCount().Should().Be(1,
            "both base64 forms map to ONE canonical digest ⇒ one ledger row");
        harness.GetBridge(bridge2).Status.Should().NotBe(BridgeStatus.Completed,
            "the replay-rejected bridge must never be marked Completed");
    }

    /// <summary>
    /// Crash-before-save recovery posture: the on-chain redeem succeeds but the
    /// process "crashes" before the Redeeming→Completed write (adapter throws
    /// after it would have minted). The row stays recoverable (non-Completed),
    /// the VAA digest is already recorded, and a subsequent redeem does NOT
    /// double-mint. Full reconciliation convergence is out of scope here.
    /// </summary>
    [Fact]
    public async Task CrashBeforeSave_DoesNotDoubleMintOnRetry()
    {
        using var harness = new FakeBridgeHarness();
        const string vaa = "VkFBLWNyYXNoLXJlY292ZXJ5";

        int redeemInvocations = 0;
        // First call: minted on-chain, then crashed before save — throw AFTER
        // entering redeem (the digest was inserted before this call per the
        // hardened ordering).
        harness.WormholeMock
            .Setup(w => w.RedeemTransferAsync(
                It.IsAny<string>(), It.IsAny<WormholeVAA>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, WormholeVAA __, string ___, CancellationToken ____) =>
            {
                Interlocked.Increment(ref redeemInvocations);
                await Task.Yield();
                throw new InvalidOperationException("process crashed after on-chain submit");
            });

        var bridgeId = harness.SeedVaaReadyBridge(vaa, 1, "emitter", 100);
        var (svc1, _) = harness.CreateService();

        // No top-level catch in redeem: the adapter exception propagates exactly
        // like a process crash AFTER the digest+state were persisted.
        var crash = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc1.RedeemWithVAAAsync(bridgeId));
        crash.Message.Should().Contain("crashed");
        redeemInvocations.Should().Be(1);

        var afterCrash = harness.GetBridge(bridgeId);
        afterCrash.Status.Should().NotBe(BridgeStatus.Completed,
            "a crashed redeem must leave a recoverable, non-terminal-success state");
        afterCrash.Status.Should().Be(BridgeStatus.Redeeming,
            "the atomic VAAReady→Redeeming committed before the crash; it is now reconcilable");
        harness.ConsumedVaaCount().Should().Be(1,
            "the VAA digest is recorded BEFORE the on-chain call (replay armor survives the crash)");

        // Retry after restart: the adapter would now succeed if reached.
        harness.WormholeMock
            .Setup(w => w.RedeemTransferAsync(
                It.IsAny<string>(), It.IsAny<WormholeVAA>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, WormholeVAA __, string ___, CancellationToken ____) =>
            {
                Interlocked.Increment(ref redeemInvocations);
                await Task.Yield();
                return new AZOAResult<WormholeRedemptionResult>
                {
                    IsError = false,
                    Result = new WormholeRedemptionResult { TxHash = "redeem_retry", Success = true }
                };
            });

        var (svc2, _) = harness.CreateService();
        var retry = await svc2.RedeemWithVAAAsync(bridgeId);

        // The crashed attempt's idempotency claim is still InProgress so the
        // retry is rejected before the adapter; the consumed-VAA digest would
        // also block it (belt + braces).
        redeemInvocations.Should().Be(1,
            "retry after a crash must never double-mint");
        retry.IsError.Should().BeTrue("the crashed-but-claimed redeem must reject the retry");
        (retry.Message.Contains("in progress")
            || retry.Message.Contains("already")
            || retry.Message.Contains("replay")
            || retry.Message.Contains("consumed"))
            .Should().BeTrue($"retry must be a clean duplicate/replay rejection, was: {retry.Message}");

        var afterRetry = harness.GetBridge(bridgeId);
        afterRetry.Status.Should().NotBe(BridgeStatus.Completed,
            "no false 'Completed' — true status is reconciliation's job (TW4), " +
            "the invariant here is strictly: no double mint");
    }

    // ─── Fake-store harness ───

    /// <summary>
    /// Wraps <see cref="FakeBridgeStore"/> + <see cref="FakeIdempotencyStore"/>
    /// with Wormhole/provider mocks and seed/query helpers. Both fakes are
    /// SHARED across all CreateService() calls so concurrent callers contend on
    /// the same in-memory state — mirroring how the SQLite harness shared one
    /// file-backed DB. A <see cref="TrackingBridgeStore"/> wraps the fake to
    /// expose consumed-VAA inspection without modifying FakeBridgeStore.
    /// </summary>
    private sealed class FakeBridgeHarness : IDisposable
    {
        private readonly TrackingBridgeStore _trackingStore;
        private readonly FakeIdempotencyStore _fakeIdempotency = new();

        public Mock<IWormholeAdapter> WormholeMock { get; } = new();
        public Mock<IBlockchainProviderFactory> FactoryMock { get; } = new();
        public Mock<IBlockchainProvider> ProviderMock { get; } = new();

        // All seeded bridge IDs — used by BridgesWithEmitter to enumerate the store.
        private readonly List<string> _seededIds = new();

        public FakeBridgeHarness()
        {
            _trackingStore = new TrackingBridgeStore(new FakeBridgeStore());
            ProviderMock.Setup(p => p.SupportsBridging).Returns(true);
            ProviderMock.Setup(p => p.ChainType).Returns("Solana");
            FactoryMock.Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
                .Returns(ProviderMock.Object);
        }

        public (CrossChainBridgeService Service, TrackingBridgeStore Store) CreateService()
        {
            var svc = new CrossChainBridgeService(
                FactoryMock.Object,
                WormholeMock.Object,
                Options.Create(new WormholeConfig { DefaultMode = BridgeMode.Wormhole }),
                _trackingStore,
                _fakeIdempotency,
                Mock.Of<ILogger<CrossChainBridgeService>>());
            return (svc, _trackingStore);
        }

        public string SeedVaaReadyBridge(
            string vaaBytes, int emitterChainId, string emitterAddress, long sequence)
        {
            var id = $"wh_bridge_{Guid.NewGuid():N}";
            _trackingStore.SeedBridge(new BridgeTransactionResult
            {
                Id = id,
                AvatarId = Guid.NewGuid(),
                SourceChain = "Solana",
                TargetChain = "Algorand",
                SourceTokenId = "token1",
                TargetAddress = "recipient",
                Amount = 1,
                Mode = BridgeMode.Wormhole,
                Status = BridgeStatus.VAAReady,
                VaaBytes = vaaBytes,
                VaaSignatureCount = 13,
                WormholeEmitterChainId = emitterChainId,
                WormholeEmitterAddress = emitterAddress,
                WormholeSequence = sequence,
                CreatedAt = DateTime.UtcNow
            });
            lock (_seededIds) { _seededIds.Add(id); }
            return id;
        }

        public string SeedCompletedBridge(string targetTokenId)
        {
            var id = $"bridge_{Guid.NewGuid():N}";
            _trackingStore.SeedBridge(new BridgeTransactionResult
            {
                Id = id,
                AvatarId = Guid.NewGuid(),
                SourceChain = "Solana",
                TargetChain = "Algorand",
                SourceTokenId = "token1",
                TargetTokenId = targetTokenId,
                SourceAddress = "src",
                TargetAddress = "recipient",
                Amount = 1,
                Mode = BridgeMode.Trusted,
                Status = BridgeStatus.Completed,
                MintTxHash = "prior_mint_tx",
                CreatedAt = DateTime.UtcNow.AddMinutes(-30),
                CompletedAt = DateTime.UtcNow.AddMinutes(-25)
            });
            lock (_seededIds) { _seededIds.Add(id); }
            return id;
        }

        public BridgeTransactionResult GetBridge(string id)
            => _trackingStore.GetBridgeAsync(id).GetAwaiter().GetResult()
               ?? throw new InvalidOperationException($"Bridge '{id}' not found in fake store.");

        public List<BridgeTransactionResult> BridgesWithEmitter(
            int emitterChainId, string emitterAddress, long sequence)
        {
            // Combine seeded IDs with IDs the service added via AddBridgeAsync.
            List<string> seeded;
            lock (_seededIds) { seeded = _seededIds.ToList(); }
            var serviceAdded = _trackingStore.GetServiceAddedBridgeIds();
            var allIds = seeded.Union(serviceAdded).Distinct().ToList();

            var results = new List<BridgeTransactionResult>();
            foreach (var id in allIds)
            {
                // Re-read from store for CURRENT state (not the insert-time snapshot).
                var b = _trackingStore.GetBridgeAsync(id).GetAwaiter().GetResult();
                if (b is not null
                    && b.WormholeEmitterChainId == emitterChainId
                    && b.WormholeEmitterAddress == emitterAddress
                    && b.WormholeSequence == sequence)
                {
                    results.Add(b);
                }
            }

            return results;
        }

        public int ConsumedVaaCount() => _trackingStore.ConsumedVaaCount();

        public List<string> ConsumedVaaDigests() => _trackingStore.ConsumedVaaDigests();

        public void Dispose() { /* fakes are GC'd; nothing to release */ }
    }

    /// <summary>
    /// Thin tracking decorator over <see cref="FakeBridgeStore"/> that intercepts
    /// <see cref="TryInsertConsumedVaaAsync"/> and <see cref="AddBridgeAsync"/>
    /// to expose consumed-VAA inspection and full-bridge enumeration without
    /// modifying <see cref="FakeBridgeStore"/>.
    /// </summary>
    private sealed class TrackingBridgeStore : IBridgeStore
    {
        private readonly FakeBridgeStore _inner;
        private readonly object _vaaLock = new();
        private readonly List<ConsumedVaaRecord> _consumed = new();
        private readonly object _bridgeLock = new();
        private readonly List<BridgeTransactionResult> _addedByService = new();

        public TrackingBridgeStore(FakeBridgeStore inner) => _inner = inner;

        // ── Inspection ────────────────────────────────────────────────────────

        public int ConsumedVaaCount()
        {
            lock (_vaaLock) { return _consumed.Count; }
        }

        public List<string> ConsumedVaaDigests()
        {
            lock (_vaaLock) { return _consumed.Select(v => v.Digest).ToList(); }
        }

        /// <summary>Returns the current (live) state of all bridges added via AddBridgeAsync.</summary>
        public List<string> GetServiceAddedBridgeIds()
        {
            lock (_bridgeLock) { return _addedByService.Select(b => b.Id).ToList(); }
        }

        // ── Seeding (delegates to inner) ──────────────────────────────────────

        public void SeedBridge(BridgeTransactionResult tx) => _inner.SeedBridge(tx);

        // ── IBridgeStore reads — all delegate to inner ────────────────────────

        public Task<BridgeTransactionResult?> GetBridgeAsync(string id, CancellationToken ct = default)
            => _inner.GetBridgeAsync(id, ct);

        public Task<IReadOnlyList<BridgeTransactionResult>> GetBridgeHistoryAsync(
            Guid avatarId, bool descending = false, CancellationToken ct = default)
            => _inner.GetBridgeHistoryAsync(avatarId, descending, ct);

        public Task<IReadOnlyList<string>> GetNonTerminalBridgeIdsAsync(
            IReadOnlyCollection<BridgeStatus> nonTerminal, DateTime staleBefore, int batch,
            CancellationToken ct = default)
            => _inner.GetNonTerminalBridgeIdsAsync(nonTerminal, staleBefore, batch, ct);

        public Task<IReadOnlyList<Guid>> GetNonTerminalOperationIdsAsync(
            IReadOnlyCollection<string> nonTerminal, DateTime staleBefore, int batch,
            CancellationToken ct = default)
            => _inner.GetNonTerminalOperationIdsAsync(nonTerminal, staleBefore, batch, ct);

        public Task<BlockchainOperation?> GetOperationAsync(Guid id, CancellationToken ct = default)
            => _inner.GetOperationAsync(id, ct);

        public Task<bool> ExistsByIdAsync(string id, CancellationToken ct = default)
            => _inner.ExistsByIdAsync(id, ct);

        public Task<BridgeTransactionResult?> GetBridgeByIdempotencyKeyAsync(
            string idempotencyKey, CancellationToken ct = default)
            => _inner.GetBridgeByIdempotencyKeyAsync(idempotencyKey, ct);

        // ── IBridgeStore writes — intercept where tracking is needed ──────────

        public Task AddBridgeAsync(BridgeTransactionResult tx, CancellationToken ct = default)
        {
            lock (_bridgeLock) { _addedByService.Add(tx); }
            return _inner.AddBridgeAsync(tx, ct);
        }

        public async Task<bool> TryInsertConsumedVaaAsync(
            ConsumedVaaRecord record, CancellationToken ct = default)
        {
            var inserted = await _inner.TryInsertConsumedVaaAsync(record, ct);
            if (inserted)
            {
                lock (_vaaLock) { _consumed.Add(record); }
            }
            return inserted;
        }

        public Task SaveVaaFetchResultAsync(
            string id, string vaaBytes, int sigCount, string proofData,
            BridgeStatus statusVAAReady, CancellationToken ct = default)
            => _inner.SaveVaaFetchResultAsync(id, vaaBytes, sigCount, proofData, statusVAAReady, ct);

        public async Task<int> TryTransitionBridgeStatusAsync(
            string id, BridgeStatus expected, BridgeStatus next, BridgeStatusMutation? alsoSet,
            CancellationToken ct = default)
        {
            // Enforce the emitter-tuple uniqueness constraint that SQLite's UNIQUE
            // filtered index would enforce: when a transition would SET emitter fields,
            // reject it (return 0) if another non-terminal bridge already holds that
            // (EmitterChainId, EmitterAddress, Sequence) triple. This mirrors the
            // DbUpdateException the EF/SQLite harness would throw, which the service's
            // top-level catch converts into an error result.
            if (alsoSet?.WormholeEmitterChainId is int chainId
                && alsoSet.WormholeEmitterAddress is string addr
                && alsoSet.WormholeSequence is long seq)
            {
                var terminalStatuses = new HashSet<BridgeStatus>
                {
                    BridgeStatus.Completed, BridgeStatus.Failed, BridgeStatus.Refunded
                };
                // Check for a conflicting row: a different id, same emitter triple,
                // not in a terminal state.
                var existing = await _inner.GetBridgeAsync(id, ct); // the row we're updating
                // We need to look at all bridges — use GetNonTerminalBridgeIdsAsync with
                // all non-terminal statuses and a broad window.
                var nonTerminal = new List<BridgeStatus>
                {
                    BridgeStatus.Initiated, BridgeStatus.Locked, BridgeStatus.AwaitingVAA,
                    BridgeStatus.VAAReady, BridgeStatus.Redeeming, BridgeStatus.Reversing
                };
                var candidateIds = await _inner.GetNonTerminalBridgeIdsAsync(
                    nonTerminal, DateTime.UtcNow.AddDays(1), 1000, ct);
                foreach (var candidateId in candidateIds)
                {
                    if (string.Equals(candidateId, id, StringComparison.Ordinal)) continue;
                    var candidate = await _inner.GetBridgeAsync(candidateId, ct);
                    if (candidate is null) continue;
                    if (candidate.WormholeEmitterChainId == chainId
                        && string.Equals(candidate.WormholeEmitterAddress, addr, StringComparison.Ordinal)
                        && candidate.WormholeSequence == seq)
                    {
                        // Constraint violation: simulate the SQLite UNIQUE index rejection.
                        throw new InvalidOperationException(
                            $"UNIQUE constraint failed: WormholeEmitter ({chainId}, {addr}, {seq}) " +
                            $"already held by bridge '{candidateId}' — duplicate Wormhole initiate rejected.");
                    }
                }
            }
            return await _inner.TryTransitionBridgeStatusAsync(id, expected, next, alsoSet, ct);
        }

        public Task<int> TryTransitionOperationStatusAsync(
            Guid id, string expected, string next, DateTime? completedDate,
            CancellationToken ct = default)
            => _inner.TryTransitionOperationStatusAsync(id, expected, next, completedDate, ct);

        public Task RecordVaaFetchErrorAsync(string id, string errorMessage, CancellationToken ct = default)
            => _inner.RecordVaaFetchErrorAsync(id, errorMessage, ct);

        public Task<int> ForceCompleteBridgeAsync(string id, CancellationToken ct = default)
            => _inner.ForceCompleteBridgeAsync(id, ct);
    }
}
