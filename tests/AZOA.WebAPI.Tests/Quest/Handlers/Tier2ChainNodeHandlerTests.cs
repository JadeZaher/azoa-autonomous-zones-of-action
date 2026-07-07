using System.Text.Json;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Quest.Handlers;
using Xunit;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Tests.Quest.Handlers;

/// <summary>
/// Tier-2 chain-action node handlers (Swap/Grant/Transfer/Refund). These assert
/// the mechanism-only contract (deserialize → call manager → serialize, no
/// economic computation), the run-context-actor invariant (the config body
/// avatar is ignored), the <c>RequiresChainCapability == true</c> tier flag, the
/// Grant Holon↔asset link (opt-in), and the soulbound-refund fail-closed path.
/// </summary>
public class Tier2ChainNodeHandlerTests
{
    private static QuestEntity QuestWithAvatarAndNode(Guid avatarId, QuestNode node) =>
        new() { Id = Guid.NewGuid(), AvatarId = avatarId, Nodes = { node } };

    private static QuestNode NodeWith(QuestNodeType type, string config) =>
        new() { Id = Guid.NewGuid(), NodeType = type, Config = config };

    private static QuestNodeExecutionContext CtxFor(QuestNode node, Guid avatarId) =>
        new(Guid.NewGuid(), node.Id, QuestWithAvatarAndNode(avatarId, node), actingAvatarId: avatarId);

    // ─── T5 Swap ───

    [Fact]
    public void SwapNodeHandler_DeclaresChainCapabilityAndType()
    {
        var handler = new SwapNodeHandler(new Mock<ISwapManager>().Object);
        handler.NodeType.Should().Be(QuestNodeType.Swap);
        ((IQuestNodeHandler)handler).RequiresChainCapability.Should().BeTrue();
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
        var node = NodeWith(QuestNodeType.Swap, JsonSerializer.Serialize(cfg));
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
        var node = NodeWith(QuestNodeType.Swap, JsonSerializer.Serialize(new SwapNodeConfig()));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("dex down");
    }

    // ─── T6 Grant ───

    [Fact]
    public void GrantNodeHandler_DeclaresChainCapabilityAndType()
    {
        var handler = new GrantNodeHandler(new Mock<INftManager>().Object, new Mock<IHolonManager>().Object);
        handler.NodeType.Should().Be(QuestNodeType.Grant);
        ((IQuestNodeHandler)handler).RequiresChainCapability.Should().BeTrue();
    }

    [Fact]
    public async Task GrantNodeHandler_Mints_WithRunContextAvatar_BodyAvatarIgnored()
    {
        var runAvatar = Guid.NewGuid();
        var nft = new Mock<INftManager>();
        nft.Setup(m => m.MintAsync(It.IsAny<NftMintRequest>(), runAvatar, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });
        var holon = new Mock<IHolonManager>();

        var handler = new GrantNodeHandler(nft.Object, holon.Object);
        // No HolonId set → link is opt-in, must be skipped.
        var node = NodeWith(QuestNodeType.Grant, JsonSerializer.Serialize(new GrantNodeConfig { Request = new NftMintRequest() }));
        var result = await handler.HandleAsync(CtxFor(node, runAvatar));

        result.IsError.Should().BeFalse();
        nft.Verify(m => m.MintAsync(It.IsAny<NftMintRequest>(), runAvatar, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()), Times.Once);
        holon.Verify(m => m.UpdateAsync(It.IsAny<Guid>(), It.IsAny<HolonUpdateModel>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()), Times.Never);
    }

    [Fact]
    public async Task GrantNodeHandler_WithHolonId_LinksTokenIdAndChainId()
    {
        var runAvatar = Guid.NewGuid();
        var holonId = Guid.NewGuid();
        var operation = new BlockchainOperation
        {
            Parameters = new Dictionary<string, string> { ["assetId"] = "9999", ["chainId"] = "algorand-mainnet" }
        };
        var nft = new Mock<INftManager>();
        nft.Setup(m => m.MintAsync(It.IsAny<NftMintRequest>(), runAvatar, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = operation });

        var holon = new Mock<IHolonManager>();
        HolonUpdateModel? capturedUpdate = null;
        holon.Setup(m => m.UpdateAsync(holonId, It.IsAny<HolonUpdateModel>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
             .Callback<Guid, HolonUpdateModel, Guid?, AZOARequest?>((_, u, _, _) => capturedUpdate = u)
             .ReturnsAsync(new AZOAResult<IHolon> { Result = new Holon { Id = holonId } });

        var handler = new GrantNodeHandler(nft.Object, holon.Object);
        var cfg = new GrantNodeConfig { Request = new NftMintRequest(), HolonId = holonId };
        var node = NodeWith(QuestNodeType.Grant, JsonSerializer.Serialize(cfg));
        var result = await handler.HandleAsync(CtxFor(node, runAvatar));

        result.IsError.Should().BeFalse();
        holon.Verify(m => m.UpdateAsync(holonId, It.IsAny<HolonUpdateModel>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()), Times.Once);
        capturedUpdate.Should().NotBeNull();
        capturedUpdate!.TokenId.Should().Be("9999");
        capturedUpdate.ChainId.Should().Be("algorand-mainnet");
    }

    [Fact]
    public async Task GrantNodeHandler_NoHolonId_DoesNotLink()
    {
        var runAvatar = Guid.NewGuid();
        var nft = new Mock<INftManager>();
        nft.Setup(m => m.MintAsync(It.IsAny<NftMintRequest>(), runAvatar, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation>
           {
               Result = new BlockchainOperation { Parameters = new Dictionary<string, string> { ["assetId"] = "1" } }
           });
        var holon = new Mock<IHolonManager>();

        var handler = new GrantNodeHandler(nft.Object, holon.Object);
        var node = NodeWith(QuestNodeType.Grant, JsonSerializer.Serialize(new GrantNodeConfig { Request = new NftMintRequest() }));
        await handler.HandleAsync(CtxFor(node, runAvatar));

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
        var node = NodeWith(QuestNodeType.Grant, JsonSerializer.Serialize(cfg));
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
        handler.NodeType.Should().Be(QuestNodeType.Transfer);
        ((IQuestNodeHandler)handler).RequiresChainCapability.Should().BeTrue();
    }

    [Fact]
    public async Task TransferNodeHandler_Transfers_WithRunContextAvatar_BodyAvatarIgnored()
    {
        var runAvatar = Guid.NewGuid();
        var nftId = Guid.NewGuid();
        var mgr = new Mock<INftManager>();
        mgr.Setup(m => m.TransferAsync(nftId, It.IsAny<NftTransferRequest>(), runAvatar, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var handler = new TransferNodeHandler(mgr.Object);
        var cfg = new TransferNodeConfig { NftId = nftId, Request = new NftTransferRequest { TargetAvatarId = Guid.NewGuid() } };
        var node = NodeWith(QuestNodeType.Transfer, JsonSerializer.Serialize(cfg));
        var result = await handler.HandleAsync(CtxFor(node, runAvatar));

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.TransferAsync(nftId, It.IsAny<NftTransferRequest>(), runAvatar, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()), Times.Once);
    }

    // ─── T8 Refund ───

    [Fact]
    public void RefundNodeHandler_DeclaresChainCapabilityAndType()
    {
        var handler = new RefundNodeHandler(new Mock<INftManager>().Object);
        handler.NodeType.Should().Be(QuestNodeType.Refund);
        ((IQuestNodeHandler)handler).RequiresChainCapability.Should().BeTrue();
    }

    [Fact]
    public async Task RefundNodeHandler_ReverseTransfer_WithRunContextAvatar()
    {
        var runAvatar = Guid.NewGuid();
        var nftId = Guid.NewGuid();
        var debitWalletId = Guid.NewGuid();
        var mgr = new Mock<INftManager>();
        // R1 guard: the soulbound read MUST be unscoped (callerAvatarId == null).
        // A runner-scoped read fails after the Transfer reassigned holon.AvatarId to
        // the recipient — modelled here by returning the NFT ONLY for the null caller.
        mgr.Setup(m => m.GetAsync(nftId, null, It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<INft> { Result = new Holon { Id = nftId, AssetType = "NFT" } });
        mgr.Setup(m => m.GetAsync(nftId, It.Is<Guid?>(a => a != null), It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<INft> { IsError = true, Message = "NFT not found." });
        mgr.Setup(m => m.TransferAsync(nftId, It.IsAny<NftTransferRequest>(), runAvatar, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        // Drain-vector guard: a Refund may ONLY reverse a REAL succeeded upstream
        // Transfer of the same NftId in this run. Build that upstream Transfer node +
        // a Succeeded execution and thread it through the run executions.
        var transferCfg = new TransferNodeConfig
        {
            NftId = nftId,
            Request = new NftTransferRequest { WalletId = debitWalletId, TargetAvatarId = Guid.NewGuid() }
        };
        var transferNode = NodeWith(QuestNodeType.Transfer, JsonSerializer.Serialize(transferCfg));
        var refundCfg = new RefundNodeConfig { NftId = nftId, Request = new NftTransferRequest { TargetAvatarId = Guid.NewGuid() } };
        var refundNode = NodeWith(QuestNodeType.Refund, JsonSerializer.Serialize(refundCfg));

        var quest = new QuestEntity { Id = Guid.NewGuid(), AvatarId = runAvatar, Nodes = { transferNode, refundNode } };
        // The linkage scans AllRunExecutions (run-wide); the real executors populate
        // it as a superset of UpstreamExecutions. A direct-predecessor Transfer lands
        // in both, so seed both here.
        var executions = new Dictionary<Guid, QuestNodeExecution>
        {
            [transferNode.Id] = new QuestNodeExecution { State = QuestNodeState.Succeeded }
        };
        var ctx = new QuestNodeExecutionContext(
            Guid.NewGuid(), refundNode.Id, quest, actingAvatarId: runAvatar,
            upstreamExecutions: executions, allRunExecutions: executions);

        var handler = new RefundNodeHandler(mgr.Object);
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeFalse();
        // Reversal is DERIVED from the debit: recipient forced to the run actor,
        // wallet reused from the upstream Transfer — never cfg.Request's direction.
        mgr.Verify(m => m.TransferAsync(
            nftId,
            It.Is<NftTransferRequest>(r => r.TargetAvatarId == runAvatar && r.WalletId == debitWalletId),
            runAvatar, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()), Times.Once);
    }

    [Fact]
    public async Task RefundNodeHandler_ReversesMultiHopTransfer_NotJustDirectPredecessor()
    {
        // R2 guard: the debit may sit MANY hops upstream (Transfer → GateCheck →
        // Refund). The linkage scans AllRunExecutions (run-wide), NOT UpstreamExecutions
        // (direct edges only). Here the Transfer is threaded through allRunExecutions
        // but deliberately NOT through upstreamExecutions — the refund must still find
        // it. A direct-edge-only scan would fail closed on this valid quest.
        var runAvatar = Guid.NewGuid();
        var nftId = Guid.NewGuid();
        var debitWalletId = Guid.NewGuid();
        var mgr = new Mock<INftManager>();
        mgr.Setup(m => m.GetAsync(nftId, null, It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<INft> { Result = new Holon { Id = nftId, AssetType = "NFT" } });
        mgr.Setup(m => m.TransferAsync(nftId, It.IsAny<NftTransferRequest>(), runAvatar, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var transferCfg = new TransferNodeConfig
        {
            NftId = nftId,
            Request = new NftTransferRequest { WalletId = debitWalletId, TargetAvatarId = Guid.NewGuid() }
        };
        var transferNode = NodeWith(QuestNodeType.Transfer, JsonSerializer.Serialize(transferCfg));
        var gateNode = NodeWith(QuestNodeType.GateCheck, "{}");
        var refundCfg = new RefundNodeConfig { NftId = nftId, Request = new NftTransferRequest { TargetAvatarId = Guid.NewGuid() } };
        var refundNode = NodeWith(QuestNodeType.Refund, JsonSerializer.Serialize(refundCfg));

        var quest = new QuestEntity { Id = Guid.NewGuid(), AvatarId = runAvatar, Nodes = { transferNode, gateNode, refundNode } };
        // Refund's DIRECT predecessor is the GateCheck; the Transfer is one hop further.
        var upstream = new Dictionary<Guid, QuestNodeExecution>
        {
            [gateNode.Id] = new QuestNodeExecution { State = QuestNodeState.Succeeded }
        };
        var allRun = new Dictionary<Guid, QuestNodeExecution>
        {
            [transferNode.Id] = new QuestNodeExecution { State = QuestNodeState.Succeeded },
            [gateNode.Id] = new QuestNodeExecution { State = QuestNodeState.Succeeded }
        };
        var ctx = new QuestNodeExecutionContext(
            Guid.NewGuid(), refundNode.Id, quest, actingAvatarId: runAvatar,
            upstreamExecutions: upstream, allRunExecutions: allRun);

        var handler = new RefundNodeHandler(mgr.Object);
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.TransferAsync(
            nftId,
            It.Is<NftTransferRequest>(r => r.TargetAvatarId == runAvatar && r.WalletId == debitWalletId),
            runAvatar, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()), Times.Once);
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
        var node = NodeWith(QuestNodeType.Refund, JsonSerializer.Serialize(cfg));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("clawback primitive");
        result.Message.Should().Contain("deferred (H2");
        mgr.Verify(m => m.TransferAsync(It.IsAny<Guid>(), It.IsAny<NftTransferRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()), Times.Never);
    }
}
