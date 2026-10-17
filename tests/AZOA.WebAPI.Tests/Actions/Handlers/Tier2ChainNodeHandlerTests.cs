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
        var handler = new RefundNodeHandler();
        handler.NodeType.Should().Be(ActionNodeType.Refund);
        handler.RequiresChainCapability.Should().BeTrue();
    }
}
