using System.Text.Json;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Services.Quest.Handlers;
using Xunit;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Tests.Quest.Handlers;

/// <summary>
/// Per-handler dispatch tests. After the quest-temporal-fork-model track,
/// handlers take a <see cref="QuestNodeExecutionContext"/> (runId+nodeId-aware)
/// and return a <see cref="QuestNodeHandlerResult"/>; the manager translates
/// the result into a <see cref="QuestNodeExecution"/> row. Handlers no longer
/// mutate <see cref="QuestNode"/> — these tests assert against the returned
/// result, not against in-place mutation.
/// </summary>
public class QuestNodeHandlerTests
{
    private static QuestEntity QuestWithAvatarAndNode(Guid avatarId, QuestNode node) =>
        new() { Id = Guid.NewGuid(), AvatarId = avatarId, Nodes = { node } };

    private static QuestNode NodeWith(QuestNodeType type, string config) =>
        new() { Id = Guid.NewGuid(), NodeType = type, Config = config };

    /// <summary>Build a context whose Quest contains <paramref name="node"/>.</summary>
    private static QuestNodeExecutionContext CtxFor(QuestNode node, Guid avatarId) =>
        new(Guid.NewGuid(), node.Id, QuestWithAvatarAndNode(avatarId, node), actingAvatarId: avatarId);

    // ─── Holon group (IHolonManager) ───

    [Fact]
    public async Task HolonCreateNodeHandler_InvokesCreate_AndMapsSuccess()
    {
        var avatarId = Guid.NewGuid();
        var holon = new Holon { Id = Guid.NewGuid(), Name = "H" };
        var mgr = new Mock<IHolonManager>();
        mgr.Setup(m => m.CreateAsync(It.IsAny<HolonCreateModel>(), avatarId, It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<IHolon> { Result = holon });

        var handler = new HolonCreateNodeHandler(mgr.Object);
        handler.NodeType.Should().Be(QuestNodeType.HolonCreate);

        var node = NodeWith(QuestNodeType.HolonCreate, JsonSerializer.Serialize(new HolonCreateModel { Name = "H" }));
        var result = await handler.HandleAsync(CtxFor(node, avatarId));

        result.IsError.Should().BeFalse();
        result.Output.Should().NotBeNull();
        mgr.Verify(m => m.CreateAsync(It.IsAny<HolonCreateModel>(), avatarId, It.IsAny<AZOARequest?>()), Times.Once);
    }

    [Fact]
    public async Task HolonCreateNodeHandler_ManagerError_MapsToFailed()
    {
        var mgr = new Mock<IHolonManager>();
        mgr.Setup(m => m.CreateAsync(It.IsAny<HolonCreateModel>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<IHolon> { IsError = true, Message = "boom" });

        var handler = new HolonCreateNodeHandler(mgr.Object);
        var node = NodeWith(QuestNodeType.HolonCreate, JsonSerializer.Serialize(new HolonCreateModel { Name = "H" }));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("boom");
    }

    [Fact]
    public async Task HolonDeleteNodeHandler_InvokesDeleteWithConfiguredId()
    {
        var id = Guid.NewGuid();
        var mgr = new Mock<IHolonManager>();
        mgr.Setup(m => m.DeleteAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<bool> { Result = true });

        var handler = new HolonDeleteNodeHandler(mgr.Object);
        var node = NodeWith(QuestNodeType.HolonDelete, JsonSerializer.Serialize(new IdConfig { Id = id }));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.DeleteAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()), Times.Once);
    }

    [Fact]
    public async Task HolonUpdateNodeHandler_DeserializesNestedConfig_AndInvokesUpdate()
    {
        var holonId = Guid.NewGuid();
        var mgr = new Mock<IHolonManager>();
        mgr.Setup(m => m.UpdateAsync(holonId, It.IsAny<HolonUpdateModel>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<IHolon> { Result = new Holon { Id = holonId } });

        var handler = new HolonUpdateNodeHandler(mgr.Object);
        var cfg = new HolonUpdateNodeConfig { HolonId = holonId, Model = new HolonUpdateModel { Name = "N" } };
        var node = NodeWith(QuestNodeType.HolonUpdate, JsonSerializer.Serialize(cfg));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.UpdateAsync(holonId, It.IsAny<HolonUpdateModel>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()), Times.Once);
    }

    // ─── NFT group (INftManager) ───

    [Fact]
    public async Task NftMintNodeHandler_InvokesMint_WithQuestAvatar()
    {
        var avatarId = Guid.NewGuid();
        var mgr = new Mock<INftManager>();
        mgr.Setup(m => m.MintAsync(It.IsAny<NftMintRequest>(), avatarId, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var handler = new NftMintNodeHandler(mgr.Object);
        handler.NodeType.Should().Be(QuestNodeType.NftMint);

        var node = NodeWith(QuestNodeType.NftMint, JsonSerializer.Serialize(new NftMintRequest()));
        var result = await handler.HandleAsync(CtxFor(node, avatarId));

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.MintAsync(It.IsAny<NftMintRequest>(), avatarId, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()), Times.Once);
    }

    [Fact]
    public async Task NftMintNodeHandler_SerializesOnlyTheSafeOperationProjection()
    {
        var avatarId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var operation = new BlockchainOperation
        {
            Id = operationId,
            AvatarId = avatarId,
            OperationType = "Mint",
            Status = "Completed",
            InitiatorApiKeyId = Guid.NewGuid(),
            Parameters = new Dictionary<string, string>
            {
                ["TxHash"] = "public-tx",
                ["IdempotencyKey"] = "alloc:private-api-key:payment-intent",
            },
        };
        var mgr = new Mock<INftManager>();
        mgr.Setup(m => m.MintAsync(It.IsAny<NftMintRequest>(), avatarId, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(AZOAResult<IBlockchainOperation>.Success(operation));
        var handler = new NftMintNodeHandler(mgr.Object);
        var node = NodeWith(QuestNodeType.NftMint, JsonSerializer.Serialize(new NftMintRequest()));

        var result = await handler.HandleAsync(CtxFor(node, avatarId));

        result.Output.Should().Contain("public-tx")
            .And.NotContain("alloc:private-api-key")
            .And.NotContain("Parameters")
            .And.NotContain("InitiatorApiKeyId");
        using var output = JsonDocument.Parse(result.Output!);
        output.RootElement.GetProperty("Result").GetProperty("Id").GetGuid()
            .Should().Be(operationId, "a downstream $from binding may still use Result.Id");
    }

    [Fact]
    public async Task NftBurnNodeHandler_InvokesBurn_WithConfiguredIds()
    {
        var nftId = Guid.NewGuid();
        var walletId = Guid.NewGuid();
        var avatarId = Guid.NewGuid();
        var mgr = new Mock<INftManager>();
        mgr.Setup(m => m.BurnAsync(nftId, walletId, avatarId, It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var handler = new NftBurnNodeHandler(mgr.Object);
        var cfg = new NftBurnNodeConfig { NftId = nftId, WalletId = walletId };
        var node = NodeWith(QuestNodeType.NftBurn, JsonSerializer.Serialize(cfg));
        var result = await handler.HandleAsync(CtxFor(node, avatarId));

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.BurnAsync(nftId, walletId, avatarId, It.IsAny<AZOARequest?>()), Times.Once);
    }

    // ─── Wallet group (IWalletManager) ───

    [Fact]
    public async Task WalletSetDefaultNodeHandler_InvokesSetDefault_WithQuestAvatarAndConfigWallet()
    {
        var avatarId = Guid.NewGuid();
        var walletId = Guid.NewGuid();
        var mgr = new Mock<IWalletManager>();
        mgr.Setup(m => m.SetDefaultAsync(avatarId, walletId, It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<bool> { Result = true });

        var handler = new WalletSetDefaultNodeHandler(mgr.Object);
        handler.NodeType.Should().Be(QuestNodeType.WalletSetDefault);

        var node = NodeWith(QuestNodeType.WalletSetDefault,
            JsonSerializer.Serialize(new WalletSetDefaultNodeConfig { WalletId = walletId }));
        var result = await handler.HandleAsync(CtxFor(node, avatarId));

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.SetDefaultAsync(avatarId, walletId, It.IsAny<AZOARequest?>()), Times.Once);
    }

    [Fact]
    public async Task WalletGetNodeHandler_ManagerError_MapsToFailed()
    {
        var mgr = new Mock<IWalletManager>();
        mgr.Setup(m => m.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<IWallet> { IsError = true, Message = "nope" });

        var handler = new WalletGetNodeHandler(mgr.Object);
        var node = NodeWith(QuestNodeType.WalletGet, JsonSerializer.Serialize(new IdConfig { Id = Guid.NewGuid() }));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("nope");
    }

    // ─── STAR group (ISTARManager) ───

    [Fact]
    public async Task StarDeployNodeHandler_InvokesDeploy_WithConfiguredId()
    {
        var id = Guid.NewGuid();
        var mgr = new Mock<ISTARManager>();
        mgr.Setup(m => m.DeployAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<ISTARODK> { Result = new STARODK() });

        var handler = new StarDeployNodeHandler(mgr.Object);
        handler.NodeType.Should().Be(QuestNodeType.StarDeploy);

        var node = NodeWith(QuestNodeType.StarDeploy, JsonSerializer.Serialize(new IdConfig { Id = id }));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.DeployAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()), Times.Once);
    }

    [Fact]
    public async Task StarGenerateNodeHandler_InvokesGenerate_WithConfiguredStarId()
    {
        var starId = Guid.NewGuid();
        var mgr = new Mock<ISTARManager>();
        mgr.Setup(m => m.GenerateAsync(starId, It.IsAny<STARDappGenerationRequest>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<ISTARODK> { Result = new STARODK() });

        var handler = new StarGenerateNodeHandler(mgr.Object);
        var cfg = new StarGenerateNodeConfig { StarId = starId, Request = new STARDappGenerationRequest() };
        var node = NodeWith(QuestNodeType.StarGenerate, JsonSerializer.Serialize(cfg));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.GenerateAsync(starId, It.IsAny<STARDappGenerationRequest>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()), Times.Once);
    }

    // ─── Search (ISearchManager) ───

    [Fact]
    public async Task SearchNodeHandler_InvokesSearch_AndMapsSuccess()
    {
        var mgr = new Mock<ISearchManager>();
        mgr.Setup(m => m.SearchAsync(It.IsAny<SearchRequest>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<SearchResult> { Result = new SearchResult() });

        var handler = new SearchNodeHandler(mgr.Object);
        handler.NodeType.Should().Be(QuestNodeType.Search);

        var node = NodeWith(QuestNodeType.Search, JsonSerializer.Serialize(new SearchRequest()));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.SearchAsync(It.IsAny<SearchRequest>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()), Times.Once);
    }

    // ─── Avatar NFT (IAvatarNFTService) ───

    [Fact]
    public async Task AvatarNFTGetCompositeNodeHandler_InvokesComposite_WithConfiguredId()
    {
        var id = Guid.NewGuid();
        var svc = new Mock<IAvatarNFTService>();
        svc.Setup(s => s.GetAvatarNFTCompositeAsync(id, It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<AvatarNFTCompositeResult> { Result = new AvatarNFTCompositeResult() });

        var handler = new AvatarNFTGetCompositeNodeHandler(svc.Object);
        handler.NodeType.Should().Be(QuestNodeType.AvatarNFTGetComposite);

        var node = NodeWith(QuestNodeType.AvatarNFTGetComposite, JsonSerializer.Serialize(new IdConfig { Id = id }));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeFalse();
        svc.Verify(s => s.GetAvatarNFTCompositeAsync(id, It.IsAny<AZOARequest?>()), Times.Once);
    }

    // ─── Blockchain (IBlockchainOperationManager) ───

    [Fact]
    public async Task BlockchainExecuteNodeHandler_InvokesGet_WithConfiguredId()
    {
        var id = Guid.NewGuid();
        var mgr = new Mock<IBlockchainOperationManager>();
        mgr.Setup(m => m.GetAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var handler = new BlockchainExecuteNodeHandler(mgr.Object);
        handler.NodeType.Should().Be(QuestNodeType.BlockchainExecute);

        var node = NodeWith(QuestNodeType.BlockchainExecute, JsonSerializer.Serialize(new IdConfig { Id = id }));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.GetAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()), Times.Once);
    }

    // ─── Control-flow (no manager dependency) ───

    [Fact]
    public async Task ConditionNodeHandler_PassesThroughConfigAsOutput()
    {
        var handler = new ConditionNodeHandler();
        handler.NodeType.Should().Be(QuestNodeType.Condition);

        var node = NodeWith(QuestNodeType.Condition, "{\"branch\":true}");
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeFalse();
        result.Output.Should().Be("{\"branch\":true}");
    }

    [Fact]
    public async Task ComposeOutputsNodeHandler_GathersUpstreamOutputs()
    {
        var handler = new ComposeOutputsNodeHandler();
        handler.NodeType.Should().Be(QuestNodeType.ComposeOutputs);

        // Upstream and target nodes live on the definition; per-run outputs
        // live on QuestNodeExecution and are passed via context.UpstreamExecutions.
        var upstreamNode = new QuestNode { Id = Guid.NewGuid(), Name = "up" };
        var node = new QuestNode { Id = Guid.NewGuid(), NodeType = QuestNodeType.ComposeOutputs, Config = "{}" };
        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Nodes = new List<QuestNode> { upstreamNode, node },
            Edges = new List<QuestEdge>
            {
                new() { Id = Guid.NewGuid(), SourceNodeId = upstreamNode.Id, TargetNodeId = node.Id }
            }
        };

        var runId = Guid.NewGuid();
        var upstreamExec = new QuestNodeExecution
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            NodeId = upstreamNode.Id,
            State = QuestNodeState.Succeeded,
            Output = "\"value\""
        };
        var ctx = new QuestNodeExecutionContext(runId, node.Id, quest, quest.AvatarId,
            new Dictionary<Guid, QuestNodeExecution> { [upstreamNode.Id] = upstreamExec });

        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeFalse();
        var composed = JsonSerializer.Deserialize<Dictionary<string, string>>(result.Output!, QuestNodeJson.Options)!;
        composed.Should().ContainKey("up");
        composed["up"].Should().Be("\"value\"");
    }
}
