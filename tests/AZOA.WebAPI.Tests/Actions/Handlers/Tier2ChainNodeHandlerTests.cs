using System.Text.Json;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions.Handlers;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions.Handlers;

/// <summary>
/// Tier-2 chain-action node handlers (Swap/Grant/Transfer/Refund). These assert
/// the mechanism-only contract (deserialize → call manager → serialize, no
/// economic computation), the run-context-actor invariant (the config body
/// user is ignored), the <c>RequiresChainCapability == true</c> tier flag, the
/// Grant Holon↔asset link (opt-in), and the soulbound-refund fail-closed path.
/// </summary>
public class Tier2ChainNodeHandlerTests
{
    private static ActionEntity ActionWithUserAndNode(Guid userId, ActionNode node) =>
        new() { Id = Guid.NewGuid(), UserId = userId, Nodes = { node } };

    private static ActionNode NodeWith(ActionNodeType type, string config) =>
        new() { Id = Guid.NewGuid(), NodeType = type, Config = config };

    private static ActionNodeExecutionContext CtxFor(ActionNode node, Guid userId) =>
        new(Guid.NewGuid(), node.Id, ActionWithUserAndNode(userId, node), actingUserId: userId);

    // ─── T5 Swap ───

    [Fact]
    public void SwapNodeHandler_DeclaresChainCapabilityAndType()
    {
        var handler = new SwapNodeHandler(new Mock<ISwapManager>().Object);
        handler.NodeType.Should().Be(ActionNodeType.Swap);
        ((IActionNodeHandler)handler).RequiresChainCapability.Should().BeTrue();
    }

    [Fact]
    public async Task SwapNodeHandler_ForwardsRequest_NoRateComputedInHandler()
    {
        var mgr = new Mock<ISwapManager>();
        SwapExecuteRequest? captured = null;
        // The mock returns the quote; the handler must NOT compute a rate itself.
        mgr.Setup(m => m.GetSwapTransactionAsync(It.IsAny<SwapExecuteRequest>(), It.IsAny<string?>()))
           .Callback<SwapExecuteRequest, string?>((req, _) => captured = req)
           .ReturnsAsync(new AZOAResult<SwapQuoteResponse> { Result = new SwapQuoteResponse() });

        var handler = new SwapNodeHandler(mgr.Object);
        var cfg = new SwapNodeConfig { Request = new SwapExecuteRequest() };
        var node = NodeWith(ActionNodeType.Swap, JsonSerializer.Serialize(cfg));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeFalse();
        captured.Should().NotBeNull();
        mgr.Verify(m => m.GetSwapTransactionAsync(It.IsAny<SwapExecuteRequest>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task SwapNodeHandler_ManagerError_MapsToFailed()
    {
        var mgr = new Mock<ISwapManager>();
        mgr.Setup(m => m.GetSwapTransactionAsync(It.IsAny<SwapExecuteRequest>(), It.IsAny<string?>()))
           .ReturnsAsync(new AZOAResult<SwapQuoteResponse> { IsError = true, Message = "dex down" });

        var handler = new SwapNodeHandler(mgr.Object);
        var node = NodeWith(ActionNodeType.Swap, JsonSerializer.Serialize(new SwapNodeConfig()));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("dex down");
    }

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
        var handler = new RefundNodeHandler(new Mock<INftManager>().Object);
        handler.NodeType.Should().Be(ActionNodeType.Refund);
        ((IActionNodeHandler)handler).RequiresChainCapability.Should().BeTrue();
    }

    [Fact]
    public async Task RefundNodeHandler_ReverseTransfer_WithRunContextUser()
    {
        var runUser = Guid.NewGuid();
        var nftId = Guid.NewGuid();
        var debitWalletId = Guid.NewGuid();
        var mgr = new Mock<INftManager>();
        // R1 guard: the soulbound read MUST be unscoped (callerUserId == null).
        // A runner-scoped read fails after the Transfer reassigned holon.UserId to
        // the recipient — modelled here by returning the NFT ONLY for the null caller.
        mgr.Setup(m => m.GetAsync(nftId, null, It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<INft> { Result = new Holon { Id = nftId, AssetType = "NFT" } });
        mgr.Setup(m => m.GetAsync(nftId, It.Is<Guid?>(a => a != null), It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<INft> { IsError = true, Message = "NFT not found." });
        mgr.Setup(m => m.TransferAsync(nftId, It.IsAny<NftTransferRequest>(), runUser, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        // Drain-vector guard: a Refund may ONLY reverse a REAL succeeded upstream
        // Transfer of the same NftId in this run. Build that upstream Transfer node +
        // a Succeeded execution and thread it through the run executions.
        var transferCfg = new TransferNodeConfig
        {
            NftId = nftId,
            Request = new NftTransferRequest { WalletId = debitWalletId, TargetUserId = Guid.NewGuid() }
        };
        var transferNode = NodeWith(ActionNodeType.Transfer, JsonSerializer.Serialize(transferCfg));
        var refundCfg = new RefundNodeConfig { NftId = nftId, Request = new NftTransferRequest { TargetUserId = Guid.NewGuid() } };
        var refundNode = NodeWith(ActionNodeType.Refund, JsonSerializer.Serialize(refundCfg));

        var action = new ActionEntity { Id = Guid.NewGuid(), UserId = runUser, Nodes = { transferNode, refundNode } };
        // The linkage scans AllRunExecutions (run-wide); the real executors populate
        // it as a superset of UpstreamExecutions. A direct-predecessor Transfer lands
        // in both, so seed both here.
        var executions = new Dictionary<Guid, ActionNodeExecution>
        {
            [transferNode.Id] = new ActionNodeExecution { State = ActionNodeState.Succeeded }
        };
        var ctx = new ActionNodeExecutionContext(
            Guid.NewGuid(), refundNode.Id, action, actingUserId: runUser,
            upstreamExecutions: executions, allRunExecutions: executions);

        var handler = new RefundNodeHandler(mgr.Object);
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeFalse();
        // Reversal is DERIVED from the debit: recipient forced to the run actor,
        // wallet reused from the upstream Transfer — never cfg.Request's direction.
        mgr.Verify(m => m.TransferAsync(
            nftId,
            It.Is<NftTransferRequest>(r => r.TargetUserId == runUser && r.WalletId == debitWalletId),
            runUser, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()), Times.Once);
    }

    [Fact]
    public async Task RefundNodeHandler_ReversesMultiHopTransfer_NotJustDirectPredecessor()
    {
        // R2 guard: the debit may sit MANY hops upstream (Transfer → GateCheck →
        // Refund). The linkage scans AllRunExecutions (run-wide), NOT UpstreamExecutions
        // (direct edges only). Here the Transfer is threaded through allRunExecutions
        // but deliberately NOT through upstreamExecutions — the refund must still find
        // it. A direct-edge-only scan would fail closed on this valid action.
        var runUser = Guid.NewGuid();
        var nftId = Guid.NewGuid();
        var debitWalletId = Guid.NewGuid();
        var mgr = new Mock<INftManager>();
        mgr.Setup(m => m.GetAsync(nftId, null, It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<INft> { Result = new Holon { Id = nftId, AssetType = "NFT" } });
        mgr.Setup(m => m.TransferAsync(nftId, It.IsAny<NftTransferRequest>(), runUser, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var transferCfg = new TransferNodeConfig
        {
            NftId = nftId,
            Request = new NftTransferRequest { WalletId = debitWalletId, TargetUserId = Guid.NewGuid() }
        };
        var transferNode = NodeWith(ActionNodeType.Transfer, JsonSerializer.Serialize(transferCfg));
        var gateNode = NodeWith(ActionNodeType.GateCheck, "{}");
        var refundCfg = new RefundNodeConfig { NftId = nftId, Request = new NftTransferRequest { TargetUserId = Guid.NewGuid() } };
        var refundNode = NodeWith(ActionNodeType.Refund, JsonSerializer.Serialize(refundCfg));

        var action = new ActionEntity { Id = Guid.NewGuid(), UserId = runUser, Nodes = { transferNode, gateNode, refundNode } };
        // Refund's DIRECT predecessor is the GateCheck; the Transfer is one hop further.
        var upstream = new Dictionary<Guid, ActionNodeExecution>
        {
            [gateNode.Id] = new ActionNodeExecution { State = ActionNodeState.Succeeded }
        };
        var allRun = new Dictionary<Guid, ActionNodeExecution>
        {
            [transferNode.Id] = new ActionNodeExecution { State = ActionNodeState.Succeeded },
            [gateNode.Id] = new ActionNodeExecution { State = ActionNodeState.Succeeded }
        };
        var ctx = new ActionNodeExecutionContext(
            Guid.NewGuid(), refundNode.Id, action, actingUserId: runUser,
            upstreamExecutions: upstream, allRunExecutions: allRun);

        var handler = new RefundNodeHandler(mgr.Object);
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.TransferAsync(
            nftId,
            It.Is<NftTransferRequest>(r => r.TargetUserId == runUser && r.WalletId == debitWalletId),
            runUser, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()), Times.Once);
    }

    [Fact]
    public async Task RefundNodeHandler_Soulbound_FailsClosed_NoTransfer()
    {
        var nftId = Guid.NewGuid();
        var mgr = new Mock<INftManager>();
        mgr.Setup(m => m.GetAsync(nftId, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<INft>
           {
               Result = new Holon
               {
                   Id = nftId,
                   AssetType = "NFT",
                   Metadata = new Dictionary<string, string> { ["soulbound"] = "true" }
               }
           });

        var handler = new RefundNodeHandler(mgr.Object);
        var cfg = new RefundNodeConfig { NftId = nftId, Request = new NftTransferRequest() };
        var node = NodeWith(ActionNodeType.Refund, JsonSerializer.Serialize(cfg));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("clawback primitive");
        result.Message.Should().Contain("deferred (H2");
        mgr.Verify(m => m.TransferAsync(It.IsAny<Guid>(), It.IsAny<NftTransferRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()), Times.Never);
    }
}
