using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Bridge;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Bridge;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Services;

/// <summary>
/// Phase C items 10-13: reverse resume / idempotency decision tree.
/// See CrossChainBridgeService.ReverseBridgeAsync for the stale-claim tree.
/// See Services/AGENTS.md §bridge-safety.
/// </summary>
public class BridgeReverseRecoveryTests
{
    private const string SourceRecipient = "source-refund-addr";

    // ═══════════════════════════════════════════════════════════════════════════
    // Item 10: Stale claim + Refunded → success replay, no burn.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReverseBridge_StaleClaimRefunded_ReturnsSuccessReplay_NoBurnCall()
    {
        using var h = new ReverseHarness(staleSeconds: 60);

        var avatarId = Guid.NewGuid();
        var bridgeId = h.SeedBridge(new BridgeTransactionResult
        {
            Id = $"bridge_{Guid.NewGuid():N}",
            AvatarId = avatarId,
            SourceChain = "Solana", TargetChain = "Algorand",
            SourceTokenId = "token1", TargetTokenId = "wrapped-token",
            SourceAddress = "src", TargetAddress = "tgt",
            Amount = 1, Mode = BridgeMode.Trusted,
            // Refunded: already reversed — stale claim still InProgress on this row.
            Status = BridgeStatus.Refunded,
            RedemptionTxHash = "prior-burn-tx",
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            CompletedAt = DateTime.UtcNow.AddMinutes(-25),
        });

        // Stale InProgress idempotency claim for the reverse operation.
        var iKey = $"bridge-reverse:{bridgeId}:{SourceRecipient}";
        h.IdempotencyStore.SeedAged(iKey, "bridge-reverse", IdempotencyState.InProgress,
            createdAt: DateTime.UtcNow.AddMinutes(-5)); // stale: 5 min > 60 s

        var result = await h.CreateService().ReverseBridgeAsync(bridgeId, SourceRecipient);

        result.IsError.Should().BeFalse("stale claim + Refunded must replay the prior success");
        result.Result!.Status.Should().Be(BridgeStatus.Refunded);

        // Idempotency must now be settled Completed.
        var record = await h.IdempotencyStore.GetAsync(iKey, default);
        record!.State.Should().Be(IdempotencyState.Completed);

        h.ProviderMock.Verify(p => p.BurnWrappedAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Item 11: Stale claim + bridge Completed → resume: BurnWrappedAsync called
    //          once, bridge ends Refunded.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReverseBridge_StaleClaimCompleted_ResumesReversal_BurnCalledOnce_EndsRefunded()
    {
        using var h = new ReverseHarness(staleSeconds: 60);

        var avatarId = Guid.NewGuid();
        var bridgeId = h.SeedBridge(new BridgeTransactionResult
        {
            Id = $"bridge_{Guid.NewGuid():N}",
            AvatarId = avatarId,
            SourceChain = "Solana", TargetChain = "Algorand",
            SourceTokenId = "token1", TargetTokenId = "wrapped-token-resume",
            SourceAddress = "src", TargetAddress = "tgt",
            Amount = 1, Mode = BridgeMode.Trusted,
            // Completed but NOT yet Reversing: the Completed→Reversing transition
            // never happened — crash before that gate.
            Status = BridgeStatus.Completed,
            MintTxHash = "prior-mint-tx",
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            CompletedAt = DateTime.UtcNow.AddMinutes(-25),
        });

        var iKey = $"bridge-reverse:{bridgeId}:{SourceRecipient}";
        h.IdempotencyStore.SeedAged(iKey, "bridge-reverse", IdempotencyState.InProgress,
            createdAt: DateTime.UtcNow.AddMinutes(-5)); // stale

        int burnCalls = 0;
        h.ProviderMock
            .Setup(p => p.BurnWrappedAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, int __, string ___, string ____, string _____, CancellationToken ______) =>
            {
                Interlocked.Increment(ref burnCalls);
                await Task.Yield();
                return new AZOAResult<string> { IsError = false, Result = "burn-resume-tx" };
            });

        var result = await h.CreateService().ReverseBridgeAsync(bridgeId, SourceRecipient);

        result.IsError.Should().BeFalse("stale Completed must resume into full reverse flow");
        result.Result!.Status.Should().Be(BridgeStatus.Refunded,
            "Completed→Reversing→Refunded is the expected terminal path");
        burnCalls.Should().Be(1, "exactly one burn call in the resume");

        h.GetBridge(bridgeId).Status.Should().Be(BridgeStatus.Refunded);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Item 12: Stale claim + Reversing → parked error, zero burn calls.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReverseBridge_StaleClaimReversing_ParkedError_ZeroBurnCalls()
    {
        using var h = new ReverseHarness(staleSeconds: 60);

        var avatarId = Guid.NewGuid();
        var bridgeId = h.SeedBridge(new BridgeTransactionResult
        {
            Id = $"bridge_{Guid.NewGuid():N}",
            AvatarId = avatarId,
            SourceChain = "Solana", TargetChain = "Algorand",
            SourceTokenId = "token1", TargetTokenId = "wrapped-token-reversing",
            SourceAddress = "src", TargetAddress = "tgt",
            Amount = 1, Mode = BridgeMode.Trusted,
            // Reversing: burn may or may not have landed — ambiguous.
            Status = BridgeStatus.Reversing,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
        });

        var iKey = $"bridge-reverse:{bridgeId}:{SourceRecipient}";
        h.IdempotencyStore.SeedAged(iKey, "bridge-reverse", IdempotencyState.InProgress,
            createdAt: DateTime.UtcNow.AddMinutes(-5)); // stale

        var result = await h.CreateService().ReverseBridgeAsync(bridgeId, SourceRecipient);

        result.IsError.Should().BeTrue("Reversing is an ambiguous crash window — must park for reconciliation");
        result.Message.Should().Contain("reconciliation",
            "message must identify the reconciliation hold");

        // Bridge must NOT advance or have a burn attempted.
        h.GetBridge(bridgeId).Status.Should().Be(BridgeStatus.Reversing,
            "the bridge must NOT be transitioned to Failed when parked for reconciliation");

        h.ProviderMock.Verify(p => p.BurnWrappedAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Item 13: Concurrent double-reverse → exactly one burn.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReverseBridge_ConcurrentDoubleReverse_ExactlyOneBurnInvocation()
    {
        using var h = new ReverseHarness(staleSeconds: 300);
        const int concurrency = 10;

        var avatarId = Guid.NewGuid();
        var bridgeId = h.SeedBridge(new BridgeTransactionResult
        {
            Id = $"bridge_{Guid.NewGuid():N}",
            AvatarId = avatarId,
            SourceChain = "Solana", TargetChain = "Algorand",
            SourceTokenId = "token1", TargetTokenId = "wrapped-concurrent",
            SourceAddress = "src", TargetAddress = "tgt",
            Amount = 1, Mode = BridgeMode.Trusted,
            Status = BridgeStatus.Completed,
            MintTxHash = "prior-mint",
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            CompletedAt = DateTime.UtcNow.AddMinutes(-25),
        });

        int burnCalls = 0;
        h.ProviderMock
            .Setup(p => p.BurnWrappedAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, int __, string ___, string ____, string _____, CancellationToken ______) =>
            {
                Interlocked.Increment(ref burnCalls);
                await Task.Yield();
                return new AZOAResult<string> { IsError = false, Result = "burn-once-tx" };
            });

        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            gate.Wait();
            return await h.CreateService().ReverseBridgeAsync(bridgeId, SourceRecipient);
        })).ToList();

        gate.Set();
        var results = await Task.WhenAll(tasks);

        burnCalls.Should().Be(1, "the Completed→Reversing atomic gate must elect a single burn owner");

        h.GetBridge(bridgeId).Status.Should().Be(BridgeStatus.Refunded);

        // Every non-error success must reference the single burn.
        results.Where(r => !r.IsError)
               .Should().NotBeEmpty("the burn owner must succeed");
        results.Where(r => !r.IsError)
               .Select(r => r.Result!.Status)
               .Should().OnlyContain(s => s == BridgeStatus.Refunded);

        // Losers must get a deterministic rejection.
        results.Where(r => r.IsError)
               .Should().OnlyContain(r =>
                   r.Message.Contains("in progress", StringComparison.OrdinalIgnoreCase)
                   || r.Message.Contains("concurrent", StringComparison.OrdinalIgnoreCase)
                   || r.Message.Contains("already", StringComparison.OrdinalIgnoreCase)
                   || r.Message.Contains("reversible", StringComparison.OrdinalIgnoreCase)
                   || r.Message.Contains("Reversing", StringComparison.OrdinalIgnoreCase)
                   || r.Message.Contains("Refunded", StringComparison.OrdinalIgnoreCase));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Internal harness
    // ═══════════════════════════════════════════════════════════════════════════

    private sealed class ReverseHarness : IDisposable
    {
        public readonly FakeBridgeStore BridgeStore = new();
        public readonly FakeIdempotencyStore IdempotencyStore = new();
        public readonly Mock<IBlockchainProvider> ProviderMock = new();
        private readonly Mock<IBlockchainProviderFactory> _factory = new();
        private readonly Mock<IWormholeAdapter> _wormhole = new();
        private readonly int _staleSeconds;

        public ReverseHarness(int staleSeconds)
        {
            _staleSeconds = staleSeconds;
            ProviderMock.Setup(p => p.SupportsBridging).Returns(true);
            ProviderMock.Setup(p => p.ChainType).Returns("Solana");
            _factory.Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
                .Returns(ProviderMock.Object);
        }

        public CrossChainBridgeService CreateService() => new(
            _factory.Object,
            _wormhole.Object,
            Options.Create(new WormholeConfig { DefaultMode = BridgeMode.Wormhole }),
            BridgeStore,
            IdempotencyStore,
            Mock.Of<ILogger<CrossChainBridgeService>>(),
            Options.Create(new BridgeOptions { RealValueEnabled = true, StaleClaimTakeoverSeconds = _staleSeconds }),
            new ConfigurationBuilder().Build());

        /// <summary>Seeds a bridge row and returns its ID.</summary>
        public string SeedBridge(BridgeTransactionResult tx)
        {
            BridgeStore.SeedBridge(tx);
            return tx.Id;
        }

        public BridgeTransactionResult GetBridge(string id)
            => BridgeStore.GetBridgeAsync(id).GetAwaiter().GetResult()
               ?? throw new InvalidOperationException($"Bridge '{id}' not found.");

        public void Dispose() { }
    }
}
