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
/// Phase C items 1-9: redeem resume / idempotency decision tree.
/// All tests go through the public service API using shared fake stores
/// (FakeBridgeStore + FakeIdempotencyStore) — same pattern as the harness
/// in CrossChainBridgeServiceTests. See Services/AGENTS.md §bridge-safety.
/// </summary>
public class BridgeRedeemRecoveryTests
{
    // ── shared VAA bytes (valid base64, deterministic digest) ──────────────────
    private const string ValidVaa = "VkFBLXJlY292ZXJ5LXRlc3Rz"; // "VAA-recovery-tests"

    // ── harness factory ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a shared-store harness so all CreateService() calls contend on the
    /// same in-memory state, exactly like the production DB.
    /// </summary>
    private static RedeemHarness BuildHarness(int staleSeconds = 120)
        => new(staleSeconds);

    // ═══════════════════════════════════════════════════════════════════════════
    // Item 1: Concurrent double-redeem — exactly one on-chain mint.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RedeemWithVAA_ConcurrentDoubleRedeem_ExactlyOneMintInvocation()
    {
        using var h = BuildHarness();
        const int concurrency = 12;

        var bridgeId = h.SeedVaaReady(ValidVaa, 1, "emitter-concurrent", 1);

        int redeemCalls = 0;
        h.WormholeMock
            .Setup(w => w.RedeemTransferAsync(
                It.IsAny<string>(), It.IsAny<WormholeVAA>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, WormholeVAA __, string ___, CancellationToken ____) =>
            {
                Interlocked.Increment(ref redeemCalls);
                await Task.Yield();
                return new AZOAResult<WormholeRedemptionResult>
                {
                    IsError = false,
                    Result = new WormholeRedemptionResult { TxHash = "mint-once", Success = true }
                };
            });

        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            gate.Wait();
            return await h.CreateService().RedeemWithVAAAsync(bridgeId);
        })).ToList();

        gate.Set();
        var results = await Task.WhenAll(tasks);

        redeemCalls.Should().Be(1, "exactly one caller must win the VAAReady→Redeeming gate");

        var bridge = h.GetBridge(bridgeId);
        bridge.Status.Should().Be(BridgeStatus.Completed);
        bridge.RedemptionTxHash.Should().Be("mint-once");

        // Every non-error outcome must reference the single mint (never a second).
        results.Where(r => !r.IsError)
               .Select(r => r.Result!.RedemptionTxHash)
               .Distinct()
               .Should().ContainSingle().Which.Should().Be("mint-once");

        // Losers get a deterministic duplicate/conflict error.
        results.Where(r => r.IsError)
               .Should().OnlyContain(r =>
                   r.Message.Contains("in progress", StringComparison.OrdinalIgnoreCase)
                   || r.Message.Contains("already", StringComparison.OrdinalIgnoreCase)
                   || r.Message.Contains("concurrent", StringComparison.OrdinalIgnoreCase)
                   || r.Message.Contains("being redeemed", StringComparison.OrdinalIgnoreCase)
                   || r.Message.Contains("VAAReady", StringComparison.OrdinalIgnoreCase));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Item 2: Fresh InProgress claim (age < threshold) → "in progress" error.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RedeemWithVAA_FreshInProgressClaim_ReturnsInProgressError_NoOnChainCall()
    {
        using var h = BuildHarness(staleSeconds: 300);

        var bridgeId = h.SeedVaaReady(ValidVaa, 1, "emitter-fresh", 2);

        // Seed a fresh InProgress claim — CreatedAt is just now so age < 300s.
        var digestKey = $"bridge-redeem:{bridgeId}:{WormholeAdapter.ComputeVaaDigest(ValidVaa)}";
        h.IdempotencyStore.SeedAged(digestKey, "bridge-redeem", IdempotencyState.InProgress,
            createdAt: DateTime.UtcNow.AddSeconds(-5)); // only 5 s old, well under 300 s

        var result = await h.CreateService().RedeemWithVAAAsync(bridgeId);

        result.IsError.Should().BeTrue("a fresh in-progress claim must be rejected");
        result.Message.Should().Contain("in progress",
            "message must signal the live in-progress claim, not an internal error");

        h.WormholeMock.Verify(w => w.RedeemTransferAsync(
            It.IsAny<string>(), It.IsAny<WormholeVAA>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Item 3: Stale claim + bridge Completed → success replay, no on-chain call.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RedeemWithVAA_StaleClaimBridgeCompleted_ReturnsSuccess_NoOnChainCall()
    {
        using var h = BuildHarness(staleSeconds: 60);

        var bridgeId = h.SeedBridge(new BridgeTransactionResult
        {
            Id = $"wh_bridge_{Guid.NewGuid():N}",
            AvatarId = Guid.NewGuid(),
            SourceChain = "Solana", TargetChain = "Algorand",
            SourceTokenId = "token1", TargetAddress = "recipient",
            Amount = 1, Mode = BridgeMode.Wormhole,
            Status = BridgeStatus.Completed,
            VaaBytes = ValidVaa,
            RedemptionTxHash = "prior-redeem-tx",
            WormholeEmitterChainId = 1, WormholeEmitterAddress = "emitter-stale-comp",
            WormholeSequence = 3, CreatedAt = DateTime.UtcNow.AddMinutes(-10),
        });

        var digestKey = $"bridge-redeem:{bridgeId}:{WormholeAdapter.ComputeVaaDigest(ValidVaa)}";
        h.IdempotencyStore.SeedAged(digestKey, "bridge-redeem", IdempotencyState.InProgress,
            createdAt: DateTime.UtcNow.AddMinutes(-5)); // stale: 5 min > 60 s

        var result = await h.CreateService().RedeemWithVAAAsync(bridgeId);

        result.IsError.Should().BeFalse("stale claim + Completed bridge must replay success");
        result.Result!.Status.Should().Be(BridgeStatus.Completed);

        // Idempotency record must be settled Completed.
        var idempRecord = await h.IdempotencyStore.GetAsync(digestKey, default);
        idempRecord!.State.Should().Be(IdempotencyState.Completed);

        h.WormholeMock.Verify(w => w.RedeemTransferAsync(
            It.IsAny<string>(), It.IsAny<WormholeVAA>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Item 4: Stale claim + bridge Failed → error replay, no on-chain call.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RedeemWithVAA_StaleClaimBridgeFailed_ReturnsError_NoOnChainCall()
    {
        using var h = BuildHarness(staleSeconds: 60);

        var bridgeId = h.SeedBridge(new BridgeTransactionResult
        {
            Id = $"wh_bridge_{Guid.NewGuid():N}",
            AvatarId = Guid.NewGuid(),
            SourceChain = "Solana", TargetChain = "Algorand",
            SourceTokenId = "token1", TargetAddress = "recipient",
            Amount = 1, Mode = BridgeMode.Wormhole,
            Status = BridgeStatus.Failed,
            VaaBytes = ValidVaa, ErrorMessage = "on-chain rejected",
            WormholeEmitterChainId = 1, WormholeEmitterAddress = "emitter-stale-fail",
            WormholeSequence = 4, CreatedAt = DateTime.UtcNow.AddMinutes(-10),
        });

        var digestKey = $"bridge-redeem:{bridgeId}:{WormholeAdapter.ComputeVaaDigest(ValidVaa)}";
        h.IdempotencyStore.SeedAged(digestKey, "bridge-redeem", IdempotencyState.InProgress,
            createdAt: DateTime.UtcNow.AddMinutes(-5)); // stale

        var result = await h.CreateService().RedeemWithVAAAsync(bridgeId);

        result.IsError.Should().BeTrue("stale claim + Failed bridge must replay the failure");

        h.WormholeMock.Verify(w => w.RedeemTransferAsync(
            It.IsAny<string>(), It.IsAny<WormholeVAA>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Item 5: Stale claim + bridge VAAReady → full resume (transition, consume,
    //         RedeemTransferAsync once, ends Completed).
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RedeemWithVAA_StaleClaimBridgeVAAReady_FullResume_Completed()
    {
        using var h = BuildHarness(staleSeconds: 60);

        var bridgeId = h.SeedBridge(new BridgeTransactionResult
        {
            Id = $"wh_bridge_{Guid.NewGuid():N}",
            AvatarId = Guid.NewGuid(),
            SourceChain = "Solana", TargetChain = "Algorand",
            SourceTokenId = "token1", TargetAddress = "recipient",
            Amount = 1, Mode = BridgeMode.Wormhole,
            // VAAReady: claim inserted but VAAReady→Redeeming never committed (crash).
            Status = BridgeStatus.VAAReady,
            VaaBytes = ValidVaa, VaaSignatureCount = 13,
            WormholeEmitterChainId = 1, WormholeEmitterAddress = "emitter-vaaready",
            WormholeSequence = 5, CreatedAt = DateTime.UtcNow.AddMinutes(-10),
        });

        var digestKey = $"bridge-redeem:{bridgeId}:{WormholeAdapter.ComputeVaaDigest(ValidVaa)}";
        h.IdempotencyStore.SeedAged(digestKey, "bridge-redeem", IdempotencyState.InProgress,
            createdAt: DateTime.UtcNow.AddMinutes(-5)); // stale

        int redeemCalls = 0;
        h.WormholeMock
            .Setup(w => w.RedeemTransferAsync(
                It.IsAny<string>(), It.IsAny<WormholeVAA>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, WormholeVAA __, string ___, CancellationToken ____) =>
            {
                Interlocked.Increment(ref redeemCalls);
                await Task.Yield();
                return new AZOAResult<WormholeRedemptionResult>
                    { IsError = false, Result = new WormholeRedemptionResult { TxHash = "vaaready-resume-tx", Success = true } };
            });

        var result = await h.CreateService().RedeemWithVAAAsync(bridgeId);

        result.IsError.Should().BeFalse("stale VAAReady must resume the full flow");
        result.Result!.Status.Should().Be(BridgeStatus.Completed);
        redeemCalls.Should().Be(1, "exactly one on-chain call in the resume path");

        var bridge = h.GetBridge(bridgeId);
        bridge.Status.Should().Be(BridgeStatus.Completed);

        // Consume ledger must have one row.
        h.BridgeStore.SeedConsumedVaa(new ConsumedVaaRecord
        {
            // Attempt a second insert to confirm UNIQUE is enforced.
            Digest = WormholeAdapter.ComputeVaaDigest(ValidVaa),
            BridgeTransactionId = bridgeId,
            EmitterChainId = 1, EmitterAddress = "emitter-vaaready", Sequence = 5,
            ConsumedAt = DateTime.UtcNow
        }).Should().BeFalse("the consume row must already exist after the resume");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Item 6: Stale claim + Redeeming + no consumed row → resume from consume
    //         step (consume-insert + RedeemTransferAsync once, ends Completed).
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RedeemWithVAA_StaleClaimRedeeming_NoConsumedRow_ResumesFromConsumeStep()
    {
        using var h = BuildHarness(staleSeconds: 60);

        var bridgeId = h.SeedBridge(new BridgeTransactionResult
        {
            Id = $"wh_bridge_{Guid.NewGuid():N}",
            AvatarId = Guid.NewGuid(),
            SourceChain = "Solana", TargetChain = "Algorand",
            SourceTokenId = "token1", TargetAddress = "recipient",
            Amount = 1, Mode = BridgeMode.Wormhole,
            // Redeeming but no consume row: VAAReady→Redeeming committed, crash before consume.
            Status = BridgeStatus.Redeeming,
            VaaBytes = ValidVaa, VaaSignatureCount = 13,
            WormholeEmitterChainId = 1, WormholeEmitterAddress = "emitter-redeeming-norow",
            WormholeSequence = 6, CreatedAt = DateTime.UtcNow.AddMinutes(-10),
        });

        var digestKey = $"bridge-redeem:{bridgeId}:{WormholeAdapter.ComputeVaaDigest(ValidVaa)}";
        h.IdempotencyStore.SeedAged(digestKey, "bridge-redeem", IdempotencyState.InProgress,
            createdAt: DateTime.UtcNow.AddMinutes(-5)); // stale

        int redeemCalls = 0;
        h.WormholeMock
            .Setup(w => w.RedeemTransferAsync(
                It.IsAny<string>(), It.IsAny<WormholeVAA>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, WormholeVAA __, string ___, CancellationToken ____) =>
            {
                Interlocked.Increment(ref redeemCalls);
                await Task.Yield();
                return new AZOAResult<WormholeRedemptionResult>
                    { IsError = false, Result = new WormholeRedemptionResult { TxHash = "redeeming-resume-tx", Success = true } };
            });

        var result = await h.CreateService().RedeemWithVAAAsync(bridgeId);

        result.IsError.Should().BeFalse("Redeeming + no consumed row means safe to resume");
        result.Result!.Status.Should().Be(BridgeStatus.Completed);
        redeemCalls.Should().Be(1, "exactly one on-chain call");

        h.GetBridge(bridgeId).Status.Should().Be(BridgeStatus.Completed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Item 7: Stale claim + Redeeming + consumed row (same bridge) → parked error,
    //         status STAYS Redeeming, zero on-chain calls, bridge NOT failed.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RedeemWithVAA_StaleClaimRedeeming_SameBridgeConsumedRow_ParkedError_StatusStaysRedeeming()
    {
        using var h = BuildHarness(staleSeconds: 60);

        var bridgeId = h.SeedBridge(new BridgeTransactionResult
        {
            Id = $"wh_bridge_{Guid.NewGuid():N}",
            AvatarId = Guid.NewGuid(),
            SourceChain = "Solana", TargetChain = "Algorand",
            SourceTokenId = "token1", TargetAddress = "recipient",
            Amount = 1, Mode = BridgeMode.Wormhole,
            Status = BridgeStatus.Redeeming,
            VaaBytes = ValidVaa, VaaSignatureCount = 13,
            WormholeEmitterChainId = 1, WormholeEmitterAddress = "emitter-parked",
            WormholeSequence = 7, CreatedAt = DateTime.UtcNow.AddMinutes(-10),
        });

        var digest = WormholeAdapter.ComputeVaaDigest(ValidVaa);

        // Consume row present and owned by THIS bridge — ambiguous crash window.
        h.BridgeStore.SeedConsumedVaa(new ConsumedVaaRecord
        {
            Digest = digest,
            BridgeTransactionId = bridgeId, // same bridge
            EmitterChainId = 1, EmitterAddress = "emitter-parked", Sequence = 7,
            ConsumedAt = DateTime.UtcNow.AddSeconds(-30)
        });

        var digestKey = $"bridge-redeem:{bridgeId}:{digest}";
        h.IdempotencyStore.SeedAged(digestKey, "bridge-redeem", IdempotencyState.InProgress,
            createdAt: DateTime.UtcNow.AddMinutes(-5)); // stale

        var result = await h.CreateService().RedeemWithVAAAsync(bridgeId);

        result.IsError.Should().BeTrue("ambiguous crash must park for reconciliation");
        result.Message.Should().Contain("reconciliation",
            "message must surface the reconciliation hold, not a generic error");

        // Bridge must stay Redeeming — NOT failed.
        var bridge = h.GetBridge(bridgeId);
        bridge.Status.Should().Be(BridgeStatus.Redeeming,
            "the bridge must NOT be transitioned to Failed on a parked-for-reconciliation outcome");

        h.WormholeMock.Verify(w => w.RedeemTransferAsync(
            It.IsAny<string>(), It.IsAny<WormholeVAA>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Item 8: Fresh claim path — consume-insert loses to a row from a DIFFERENT
    //         bridge → bridge→Failed (cross-bridge replay rejected), no on-chain.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RedeemWithVAA_FreshClaim_ConsumeInsertLosesToDifferentBridge_BridgeFailed_NoOnChain()
    {
        using var h = BuildHarness();

        // Pre-consume the VAA under a different bridge.
        var otherBridgeId = $"wh_bridge_{Guid.NewGuid():N}";
        var digest = WormholeAdapter.ComputeVaaDigest(ValidVaa);
        h.BridgeStore.SeedConsumedVaa(new ConsumedVaaRecord
        {
            Digest = digest,
            BridgeTransactionId = otherBridgeId, // different bridge!
            EmitterChainId = 2, EmitterAddress = "other-emitter", Sequence = 99,
            ConsumedAt = DateTime.UtcNow.AddMinutes(-1)
        });

        // This bridge tries to redeem the same VAA → must be rejected as cross-bridge replay.
        var bridgeId = h.SeedBridge(new BridgeTransactionResult
        {
            Id = $"wh_bridge_{Guid.NewGuid():N}",
            AvatarId = Guid.NewGuid(),
            SourceChain = "Solana", TargetChain = "Algorand",
            SourceTokenId = "token1", TargetAddress = "recipient",
            Amount = 1, Mode = BridgeMode.Wormhole,
            Status = BridgeStatus.VAAReady,
            VaaBytes = ValidVaa, VaaSignatureCount = 13,
            WormholeEmitterChainId = 1, WormholeEmitterAddress = "emitter-cross",
            WormholeSequence = 8, CreatedAt = DateTime.UtcNow.AddMinutes(-2),
        });

        var result = await h.CreateService().RedeemWithVAAAsync(bridgeId);

        result.IsError.Should().BeTrue("cross-bridge VAA replay must be rejected");
        result.Message.Should().Contain("replay",
            "message must identify this as a replay rejection");

        h.GetBridge(bridgeId).Status.Should().Be(BridgeStatus.Failed,
            "the bridge must transition to Failed on cross-bridge replay");

        h.WormholeMock.Verify(w => w.RedeemTransferAsync(
            It.IsAny<string>(), It.IsAny<WormholeVAA>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Item 9: FetchVAA lost-race — SaveVaaFetchResult returns false + row already
    //         VAAReady → idempotent success (no overwrite).
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FetchVAA_SaveLostRace_RowAlreadyVAAReady_IdempotentSuccess()
    {
        using var h = BuildHarness();

        // Seed a bridge in AwaitingVAA — FetchVAA will attempt SaveVaaFetchResultAsync.
        var bridgeId = h.SeedBridge(new BridgeTransactionResult
        {
            Id = $"wh_bridge_{Guid.NewGuid():N}",
            AvatarId = Guid.NewGuid(),
            SourceChain = "Solana", TargetChain = "Algorand",
            SourceTokenId = "token1", TargetAddress = "recipient",
            Amount = 1, Mode = BridgeMode.Wormhole,
            Status = BridgeStatus.AwaitingVAA,
            WormholeEmitterChainId = 1, WormholeEmitterAddress = "emitter-fetchrace",
            WormholeSequence = 9, CreatedAt = DateTime.UtcNow.AddMinutes(-2),
        });

        // Mock FetchVAAAsync to return a valid VAA.
        h.WormholeMock
            .Setup(w => w.FetchVAAAsync(1, "emitter-fetchrace", 9, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<WormholeVAA>
            {
                IsError = false,
                Result = new WormholeVAA
                {
                    VaaBytes = ValidVaa, SignatureCount = 13,
                    Sequence = 9, EmitterChainId = 1,
                    EmitterAddress = "emitter-fetchrace",
                    Digest = WormholeAdapter.ComputeVaaDigest(ValidVaa)
                }
            });

        // A concurrent call wins and advances the row to VAAReady before our save.
        // Simulate by manually advancing the row NOW (before FetchVAA runs the save).
        // FakeBridgeStore.SaveVaaFetchResultAsync only saves when status == AwaitingVAA;
        // we force the row to VAAReady so the save predicate fails (returns false).
        h.BridgeStore.SeedBridge(new BridgeTransactionResult
        {
            Id = bridgeId,
            AvatarId = Guid.NewGuid(),
            SourceChain = "Solana", TargetChain = "Algorand",
            SourceTokenId = "token1", TargetAddress = "recipient",
            Amount = 1, Mode = BridgeMode.Wormhole,
            // Already VAAReady — concurrent fetch already saved the VAA.
            Status = BridgeStatus.VAAReady,
            VaaBytes = ValidVaa, VaaSignatureCount = 13,
            WormholeEmitterChainId = 1, WormholeEmitterAddress = "emitter-fetchrace",
            WormholeSequence = 9, CreatedAt = DateTime.UtcNow.AddMinutes(-2),
        });

        var result = await h.CreateService().FetchVAAAsync(bridgeId);

        // Must be an idempotent success: the concurrent call already saved VAAReady,
        // and the service re-reads the row and returns OK for VAAReady-or-later states.
        result.IsError.Should().BeFalse(
            "when SaveVaaFetchResult returns false and the row is already VAAReady, " +
            "the service must treat this as an idempotent success");
        result.Result!.Status.Should().Be(BridgeStatus.VAAReady);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Internal harness — mirrors FakeBridgeHarness in CrossChainBridgeServiceTests
    // ═══════════════════════════════════════════════════════════════════════════

    private sealed class RedeemHarness : IDisposable
    {
        public readonly FakeBridgeStore BridgeStore = new();
        public readonly FakeIdempotencyStore IdempotencyStore = new();
        public readonly Mock<IWormholeAdapter> WormholeMock = new();
        private readonly Mock<IBlockchainProviderFactory> _factory = new();
        private readonly Mock<IBlockchainProvider> _provider = new();
        private readonly int _staleSeconds;

        public RedeemHarness(int staleSeconds)
        {
            _staleSeconds = staleSeconds;
            _provider.Setup(p => p.SupportsBridging).Returns(true);
            _provider.Setup(p => p.ChainType).Returns("Solana");
            _factory.Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
                .Returns(_provider.Object);
        }

        public CrossChainBridgeService CreateService() => new(
            _factory.Object,
            WormholeMock.Object,
            Options.Create(new WormholeConfig { DefaultMode = BridgeMode.Wormhole }),
            BridgeStore,
            IdempotencyStore,
            Mock.Of<ILogger<CrossChainBridgeService>>(),
            Options.Create(new BridgeOptions { RealValueEnabled = true, StaleClaimTakeoverSeconds = _staleSeconds }),
            new ConfigurationBuilder().Build(),
            new ApprovedRealValueKycGate());

        /// <summary>Seeds a VAAReady bridge and returns its ID.</summary>
        public string SeedVaaReady(string vaaBytes, int emitterChainId, string emitterAddress, long sequence)
        {
            var id = $"wh_bridge_{Guid.NewGuid():N}";
            BridgeStore.SeedBridge(new BridgeTransactionResult
            {
                Id = id, AvatarId = Guid.NewGuid(),
                SourceChain = "Solana", TargetChain = "Algorand",
                SourceTokenId = "token1", TargetAddress = "recipient",
                Amount = 1, Mode = BridgeMode.Wormhole,
                Status = BridgeStatus.VAAReady,
                VaaBytes = vaaBytes, VaaSignatureCount = 13,
                WormholeEmitterChainId = emitterChainId,
                WormholeEmitterAddress = emitterAddress,
                WormholeSequence = sequence,
                CreatedAt = DateTime.UtcNow,
            });
            return id;
        }

        /// <summary>Seeds an arbitrary bridge row and returns its ID.</summary>
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
