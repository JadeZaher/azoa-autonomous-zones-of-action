using System.Text.Json;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions.Handlers;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions.Handlers;
public class Tier2ChainNodeHandlerTests
{
    private static ActionEntity ActionWithUserAndNode(Guid userId, ActionNode node) =>
        new() { Id = Guid.NewGuid(), UserId = userId, Nodes = { node } };

    private static ActionNode NodeWith(ActionNodeType type, string config) =>
        new() { Id = Guid.NewGuid(), NodeType = type, Config = config };

    private static ActionNodeExecutionContext CtxFor(ActionNode node, Guid userId) =>
        new(Guid.NewGuid(), node.Id, ActionWithUserAndNode(userId, node), actingUserId: userId);

    // T5 Swap: covered as a durable workflow flow in Managers/Dex/SwapActionResolverTests.

    // ─── T6 Grant ───

    [Fact]
    public void GrantNodeHandler_DeclaresChainCapabilityAndType()
    {
        var handler = new GrantNodeHandler(new Mock<INftManager>().Object, new Mock<IHolonManager>().Object);
        handler.NodeType.Should().Be(ActionNodeType.Grant);
        ((IActionNodeHandler)handler).RequiresChainCapability.Should().BeTrue();
    }

    [Fact]
    public async Task GrantNodeHandler_Mints_WithRunContextUser_BodyUserIgnored()
    {
        var runUser = Guid.NewGuid();
        var nft = new Mock<INftManager>();
        nft.Setup(m => m.MintAsync(It.IsAny<NftMintRequest>(), runUser, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });
        var holon = new Mock<IHolonManager>();

        var handler = new GrantNodeHandler(nft.Object, holon.Object);
        // No HolonId set → link is opt-in, must be skipped.
        var node = NodeWith(ActionNodeType.Grant, JsonSerializer.Serialize(new GrantNodeConfig { Request = new NftMintRequest() }));
        var result = await handler.HandleAsync(CtxFor(node, runUser));

        result.IsError.Should().BeFalse();
        nft.Verify(m => m.MintAsync(It.IsAny<NftMintRequest>(), runUser, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()), Times.Once);
        holon.Verify(m => m.UpdateAsync(It.IsAny<Guid>(), It.IsAny<HolonUpdateModel>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()), Times.Never);
    }

    [Fact]
    public async Task GrantNodeHandler_WithHolonId_LinksTokenIdAndChainId()
    {
        var runUser = Guid.NewGuid();
        var holonId = Guid.NewGuid();
        var operation = new BlockchainOperation
        {
            Parameters = new Dictionary<string, string> { ["assetId"] = "9999", ["chainId"] = "algorand-mainnet" }
        };
        var nft = new Mock<INftManager>();
        nft.Setup(m => m.MintAsync(It.IsAny<NftMintRequest>(), runUser, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = operation });

        var holon = new Mock<IHolonManager>();
        HolonUpdateModel? capturedUpdate = null;
        holon.Setup(m => m.UpdateAsync(holonId, It.IsAny<HolonUpdateModel>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
             .Callback<Guid, HolonUpdateModel, Guid?, AZOARequest?>((_, u, _, _) => capturedUpdate = u)
             .ReturnsAsync(new AZOAResult<IHolon> { Result = new Holon { Id = holonId } });

        var handler = new GrantNodeHandler(nft.Object, holon.Object);
        var cfg = new GrantNodeConfig { Request = new NftMintRequest(), HolonId = holonId };
        var node = NodeWith(ActionNodeType.Grant, JsonSerializer.Serialize(cfg));
        var result = await handler.HandleAsync(CtxFor(node, runUser));

        result.IsError.Should().BeFalse();
        holon.Verify(m => m.UpdateAsync(holonId, It.IsAny<HolonUpdateModel>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()), Times.Once);
        capturedUpdate.Should().NotBeNull();
        capturedUpdate!.TokenId.Should().Be("9999");
        capturedUpdate.ChainId.Should().Be("algorand-mainnet");
    }

    [Fact]
    public async Task GrantNodeHandler_NoHolonId_DoesNotLink()
    {
        var runUser = Guid.NewGuid();
        var nft = new Mock<INftManager>();
        nft.Setup(m => m.MintAsync(It.IsAny<NftMintRequest>(), runUser, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation>
           {
               Result = new BlockchainOperation { Parameters = new Dictionary<string, string> { ["assetId"] = "1" } }
           });
        var holon = new Mock<IHolonManager>();

        var handler = new GrantNodeHandler(nft.Object, holon.Object);
        var node = NodeWith(ActionNodeType.Grant, JsonSerializer.Serialize(new GrantNodeConfig { Request = new NftMintRequest() }));
        await handler.HandleAsync(CtxFor(node, runUser));

        holon.Verify(m => m.UpdateAsync(It.IsAny<Guid>(), It.IsAny<HolonUpdateModel>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()), Times.Never);
    }

    [Fact]
    public async Task GrantNodeHandler_MintError_MapsToFailed_NoLink()
    {
        var nft = new Mock<INftManager>();
        nft.Setup(m => m.MintAsync(It.IsAny<NftMintRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { IsError = true, Message = "kyc fail" });
        var holon = new Mock<IHolonManager>();

        var handler = new GrantNodeHandler(nft.Object, holon.Object);
        var cfg = new GrantNodeConfig { Request = new NftMintRequest(), HolonId = Guid.NewGuid() };
        var node = NodeWith(ActionNodeType.Grant, JsonSerializer.Serialize(cfg));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("kyc fail");
        holon.Verify(m => m.UpdateAsync(It.IsAny<Guid>(), It.IsAny<HolonUpdateModel>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()), Times.Never);
    }

    // ─── T7 Transfer ───

    [Fact]
    public void TransferNodeHandler_DeclaresChainCapabilityAndType()
    {
        var handler = new TransferNodeHandler(new Mock<INftManager>().Object);
        handler.NodeType.Should().Be(ActionNodeType.Transfer);
        ((IActionNodeHandler)handler).RequiresChainCapability.Should().BeTrue();
    }

    [Fact]
    public async Task TransferNodeHandler_Transfers_WithRunContextUser_BodyUserIgnored()
    {
        var runUser = Guid.NewGuid();
        var nftId = Guid.NewGuid();
        var mgr = new Mock<INftManager>();
        mgr.Setup(m => m.TransferAsync(nftId, It.IsAny<NftTransferRequest>(), runUser, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var handler = new TransferNodeHandler(mgr.Object);
        var cfg = new TransferNodeConfig { NftId = nftId, Request = new NftTransferRequest { TargetUserId = Guid.NewGuid() } };
        var node = NodeWith(ActionNodeType.Transfer, JsonSerializer.Serialize(cfg));
        var result = await handler.HandleAsync(CtxFor(node, runUser));

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.TransferAsync(nftId, It.IsAny<NftTransferRequest>(), runUser, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()), Times.Once);
    }

    // ─── T8 Refund ───

    [Fact]
    public void RefundNodeHandler_DeclaresChainCapabilityAndType()
    {
        var handler = new RefundNodeHandler(Mock.Of<INftLifecycleService>());
        handler.NodeType.Should().Be(ActionNodeType.Refund);
        handler.RequiresChainCapability.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refund_UsesExactSucceededSourceLifecycle_RegardlessOfDirectPredecessor(bool multiHop)
    {
        var actor = Guid.NewGuid(); var nftId = Guid.NewGuid(); var runId = Guid.NewGuid();
        var source = NodeWith(ActionNodeType.Transfer, JsonSerializer.Serialize(new TransferNodeConfig { NftId = nftId, Request = new() }));
        var refund = NodeWith(ActionNodeType.Refund, JsonSerializer.Serialize(new RefundNodeConfig { NftId = nftId, Request = new() { TargetUserId = Guid.NewGuid() } }));
        var action = new ActionEntity { Id = Guid.NewGuid(), UserId = actor, Nodes = { source, refund } };
        var succeeded = new Dictionary<Guid, ActionNodeExecution> { [source.Id] = new() { State = ActionNodeState.Succeeded } };
        var context = new ActionNodeExecutionContext(runId, refund.Id, action, actingUserId: actor,
            upstreamExecutions: multiHop ? new Dictionary<Guid, ActionNodeExecution>() : succeeded, allRunExecutions: succeeded);
        var service = new Mock<INftLifecycleService>();
        service.Setup(item => item.RefundAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<Guid>(), It.IsAny<NftFeeAuthorization?>(), It.IsAny<ActionEffectAuthority?>(), It.IsAny<AZOA.WebAPI.Models.Signing.SigningCredentialWitness?>()))
            .ReturnsAsync(AZOAResult<NftLifecycleView>.Success(new(Guid.NewGuid().ToString("N"), nftId, "Completed", "Transfer", "asset", "chain-tx", null, "Refund")));
        var result = await new RefundNodeHandler(service.Object).HandleAsync(context);
        result.IsError.Should().BeFalse();
        var expectedSource = NftLifecycleIdentity.ForRequest(actor, "Transfer", $"{runId:N}:{source.Id:N}");
        service.Verify(item => item.RefundAsync(actor, expectedSource, $"{runId:N}:{refund.Id:N}", It.IsAny<CancellationToken>(), Guid.Empty, null, null, null), Times.Once);
    }

    [Theory]
    [InlineData("AwaitingRefundAuthorization")]
    [InlineData("Submitted")]
    public async Task Refund_PendingParticipantOrChainEvidence_IsNotSuccess(string state)
    {
        var actor = Guid.NewGuid(); var nftId = Guid.NewGuid();
        var source = NodeWith(ActionNodeType.Transfer, JsonSerializer.Serialize(new TransferNodeConfig { NftId = nftId, Request = new() }));
        var refund = NodeWith(ActionNodeType.Refund, JsonSerializer.Serialize(new RefundNodeConfig { NftId = nftId, Request = new() }));
        var action = new ActionEntity { Id = Guid.NewGuid(), UserId = actor, Nodes = { source, refund } };
        var all = new Dictionary<Guid, ActionNodeExecution> { [source.Id] = new() { State = ActionNodeState.Succeeded } };
        var service = new Mock<INftLifecycleService>();
        service.Setup(item => item.RefundAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<Guid>(), It.IsAny<NftFeeAuthorization?>(), It.IsAny<ActionEffectAuthority?>(), It.IsAny<AZOA.WebAPI.Models.Signing.SigningCredentialWitness?>()))
            .ReturnsAsync(AZOAResult<NftLifecycleView>.Success(new(Guid.NewGuid().ToString("N"), nftId, state, "Transfer", "asset", null, null, "Refund")));
        var result = await new RefundNodeHandler(service.Object).HandleAsync(new(Guid.NewGuid(), refund.Id, action, actingUserId: actor, upstreamExecutions: all, allRunExecutions: all));
        result.IsError.Should().BeTrue(); result.Message.Should().Contain(state);
    }

    [Fact]
    public async Task Refund_WithoutSucceededSameAssetSource_NeverCallsLifecycle()
    {
        var service = new Mock<INftLifecycleService>();
        var node = NodeWith(ActionNodeType.Refund, JsonSerializer.Serialize(new RefundNodeConfig { NftId = Guid.NewGuid(), Request = new() }));
        var result = await new RefundNodeHandler(service.Object).HandleAsync(CtxFor(node, Guid.NewGuid()));
        result.IsError.Should().BeTrue();
        service.Verify(item => item.RefundAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<Guid>(), It.IsAny<NftFeeAuthorization?>(), It.IsAny<ActionEffectAuthority?>(), It.IsAny<AZOA.WebAPI.Models.Signing.SigningCredentialWitness?>()), Times.Never);
    }
}
