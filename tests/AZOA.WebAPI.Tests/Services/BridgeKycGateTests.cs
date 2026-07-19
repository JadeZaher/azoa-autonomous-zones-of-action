using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Bridge;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Bridge;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AZOA.WebAPI.Tests.Services;

public sealed class BridgeKycGateTests
{
    [Fact]
    public async Task Initiate_ExpiredApproval_DeniesBeforeClaimPersistenceOrChainEffect()
    {
        var harness = new Harness(DeniedGate());
        var avatarId = Guid.NewGuid();

        var result = await harness.Service.InitiateBridgeAsync(
            "Algorand", "Solana", "token", "recipient", avatarId,
            mode: BridgeMode.Trusted);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(KycAuthorizationError.Forbidden);
        harness.Gate.Verify(item => item.RequireCurrentApprovalAsync(
            avatarId, It.IsAny<CancellationToken>()), Times.Once);
        harness.AssertNoValueEffect();
    }

    [Fact]
    public async Task Redeem_UnavailableAuthority_DeniesTransactionOwnerBeforeClaimOrChainEffect()
    {
        var harness = new Harness(DeniedGate());
        var owner = Guid.NewGuid();
        const string bridgeId = "bridge-redeem-kyc";
        harness.BridgeStore.Setup(item => item.GetBridgeAsync(
                bridgeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BridgeTransactionResult
            {
                Id = bridgeId,
                AvatarId = owner,
                SourceChain = "Algorand",
                TargetChain = "Solana",
                Mode = BridgeMode.Wormhole,
                Status = BridgeStatus.VAAReady,
                VaaBytes = "dmFsaWQtdmFh",
            });

        var result = await harness.Service.RedeemWithVAAAsync(bridgeId);

        result.IsError.Should().BeTrue();
        harness.Gate.Verify(item => item.RequireCurrentApprovalAsync(
            owner, It.IsAny<CancellationToken>()), Times.Once);
        harness.AssertNoValueEffect();
    }

    [Fact]
    public async Task Reverse_RevokedApproval_DeniesTransactionOwnerBeforeClaimOrChainEffect()
    {
        var harness = new Harness(DeniedGate());
        var owner = Guid.NewGuid();
        const string bridgeId = "bridge-reverse-kyc";
        harness.BridgeStore.Setup(item => item.GetBridgeAsync(
                bridgeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BridgeTransactionResult
            {
                Id = bridgeId,
                AvatarId = owner,
                SourceChain = "Algorand",
                TargetChain = "Solana",
                SourceTokenId = "source-token",
                TargetTokenId = "wrapped-token",
                Amount = 1,
                Mode = BridgeMode.Trusted,
                Status = BridgeStatus.Completed,
            });

        var result = await harness.Service.ReverseBridgeAsync(bridgeId, "refund-address");

        result.IsError.Should().BeTrue();
        harness.Gate.Verify(item => item.RequireCurrentApprovalAsync(
            owner, It.IsAny<CancellationToken>()), Times.Once);
        harness.AssertNoValueEffect();
    }

    [Fact]
    public async Task SimulatedInitiate_DoesNotConsultRealValueKycGate()
    {
        var gate = new Mock<IRealValueKycGate>(MockBehavior.Strict);
        var harness = new Harness(gate, "Simulated");
        harness.Idempotency.Setup(item => item.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdempotencyClaim(false, new IdempotencyRecord
            {
                Key = "simulated-replay",
                OperationType = "bridge-trusted",
                State = IdempotencyState.Failed,
                Error = "stop after simulated gate bypass",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }));

        await harness.Service.InitiateBridgeAsync(
            "SimulatedA", "SimulatedB", "token", "recipient", Guid.NewGuid(),
            mode: BridgeMode.Trusted);

        gate.VerifyNoOtherCalls();
        harness.Idempotency.Verify(item => item.TryClaimAsync(
            It.IsAny<string>(), "bridge-trusted", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Mock<IRealValueKycGate> DeniedGate()
    {
        var gate = new Mock<IRealValueKycGate>();
        gate.Setup(item => item.RequireCurrentApprovalAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Failure(
                KycAuthorizationError.Forbidden
                + KycAuthorizationError.VerificationRequiredMessage));
        return gate;
    }

    private sealed class Harness
    {
        public Harness(Mock<IRealValueKycGate> gate, string chainType = "Algorand")
        {
            Gate = gate;
            Provider.SetupGet(item => item.ChainType).Returns(chainType);
            Provider.SetupGet(item => item.SupportsBridging).Returns(true);
            Factory.Setup(item => item.GetProvider(
                    It.IsAny<string>(), It.IsAny<ChainNetwork>()))
                .Returns(Provider.Object);

            Service = new CrossChainBridgeService(
                Factory.Object,
                Wormhole.Object,
                Options.Create(new WormholeConfig { DefaultMode = BridgeMode.Trusted }),
                BridgeStore.Object,
                Idempotency.Object,
                Mock.Of<ILogger<CrossChainBridgeService>>(),
                Options.Create(new BridgeOptions { RealValueEnabled = true }),
                new ConfigurationBuilder().Build(),
                Gate.Object);
        }

        public CrossChainBridgeService Service { get; }
        public Mock<IRealValueKycGate> Gate { get; }
        public Mock<IBlockchainProviderFactory> Factory { get; } = new();
        public Mock<IBlockchainProvider> Provider { get; } = new();
        public Mock<IWormholeAdapter> Wormhole { get; } = new();
        public Mock<IBridgeStore> BridgeStore { get; } = new();
        public Mock<IIdempotencyStore> Idempotency { get; } = new();

        public void AssertNoValueEffect()
        {
            Idempotency.Verify(item => item.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            BridgeStore.Verify(item => item.AddBridgeAsync(
                It.IsAny<BridgeTransactionResult>(), It.IsAny<CancellationToken>()), Times.Never);
            BridgeStore.Verify(item => item.TryTransitionBridgeStatusAsync(
                It.IsAny<string>(), It.IsAny<BridgeStatus>(), It.IsAny<BridgeStatus>(),
                It.IsAny<BridgeStatusMutation?>(), It.IsAny<CancellationToken>()), Times.Never);
            Provider.Verify(item => item.LockForBridgeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ulong>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            Provider.Verify(item => item.MintWrappedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            Provider.Verify(item => item.BurnWrappedAsync(
                It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            Provider.Verify(item => item.ReleaseFromBridgeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ulong>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            Wormhole.Verify(item => item.RedeemTransferAsync(
                It.IsAny<string>(), It.IsAny<WormholeVAA>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
