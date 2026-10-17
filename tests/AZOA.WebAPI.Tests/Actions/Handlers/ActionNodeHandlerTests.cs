using System.Text.Json;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Handlers;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions.Handlers;

/// <summary>
/// Per-handler dispatch tests. After the action-temporal-fork-model track,
/// handlers take a <see cref="ActionNodeExecutionContext"/> (runId+nodeId-aware)
/// and return a <see cref="ActionNodeHandlerResult"/>; the manager translates
/// the result into a <see cref="ActionNodeExecution"/> row. Handlers no longer
/// mutate <see cref="ActionNode"/> — these tests assert against the returned
/// result, not against in-place mutation.
/// </summary>
public class ActionNodeHandlerTests
{
    private static ActionEntity ActionWithUserAndNode(Guid userId, ActionNode node) =>
        new() { Id = Guid.NewGuid(), UserId = userId, Nodes = { node } };

    private static ActionNode NodeWith(ActionNodeType type, string config) =>
        new() { Id = Guid.NewGuid(), NodeType = type, Config = config };

    /// <summary>Build a context whose ActionDefinition contains <paramref name="node"/>.</summary>
    private static ActionNodeExecutionContext CtxFor(ActionNode node, Guid userId) =>
        new(Guid.NewGuid(), node.Id, ActionWithUserAndNode(userId, node), actingUserId: userId);

    // ─── Holon group (IHolonManager) ───

    [Fact]
    public async Task HolonCreateNodeHandler_InvokesCreate_AndMapsSuccess()
    {
        var userId = Guid.NewGuid();
        var holon = new Holon { Id = Guid.NewGuid(), Name = "H" };
        var mgr = new Mock<IHolonManager>();
        mgr.Setup(m => m.CreateAsync(It.IsAny<HolonCreateModel>(), userId, It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<IHolon> { Result = holon });

        var handler = new HolonCreateNodeHandler(mgr.Object);
        handler.NodeType.Should().Be(ActionNodeType.HolonCreate);

        var node = NodeWith(ActionNodeType.HolonCreate, JsonSerializer.Serialize(new HolonCreateModel { Name = "H" }));
        var result = await handler.HandleAsync(CtxFor(node, userId));

        result.IsError.Should().BeFalse();
        result.Output.Should().NotBeNull();
        mgr.Verify(m => m.CreateAsync(It.IsAny<HolonCreateModel>(), userId, It.IsAny<AZOARequest?>()), Times.Once);
    }

    [Fact]
    public async Task HolonCreateNodeHandler_ManagerError_MapsToFailed()
    {
        var mgr = new Mock<IHolonManager>();
        mgr.Setup(m => m.CreateAsync(It.IsAny<HolonCreateModel>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<IHolon> { IsError = true, Message = "boom" });

        var handler = new HolonCreateNodeHandler(mgr.Object);
        var node = NodeWith(ActionNodeType.HolonCreate, JsonSerializer.Serialize(new HolonCreateModel { Name = "H" }));
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
        var node = NodeWith(ActionNodeType.HolonDelete, JsonSerializer.Serialize(new IdConfig { Id = id }));
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
        var node = NodeWith(ActionNodeType.HolonUpdate, JsonSerializer.Serialize(cfg));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.UpdateAsync(holonId, It.IsAny<HolonUpdateModel>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()), Times.Once);
    }

    // ─── NFT group (INftManager) ───

    [Fact]
    public async Task NftMintNodeHandler_InvokesMint_WithActionUser()
    {
        var userId = Guid.NewGuid();
        var mgr = new Mock<INftManager>();
        mgr.Setup(m => m.MintAsync(It.IsAny<NftMintRequest>(), userId, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var handler = new NftMintNodeHandler(mgr.Object);
        handler.NodeType.Should().Be(ActionNodeType.NftMint);

        var node = NodeWith(ActionNodeType.NftMint, JsonSerializer.Serialize(new NftMintRequest()));
        var result = await handler.HandleAsync(CtxFor(node, userId));

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.MintAsync(It.IsAny<NftMintRequest>(), userId, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()), Times.Once);
    }

    [Fact]
    public async Task NftMintNodeHandler_SerializesOnlyTheSafeOperationProjection()
    {
        var userId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var operation = new BlockchainOperation
        {
            Id = operationId,
            UserId = userId,
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
        mgr.Setup(m => m.MintAsync(It.IsAny<NftMintRequest>(), userId, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(AZOAResult<IBlockchainOperation>.Success(operation));
        var handler = new NftMintNodeHandler(mgr.Object);
        var node = NodeWith(ActionNodeType.NftMint, JsonSerializer.Serialize(new NftMintRequest()));

        var result = await handler.HandleAsync(CtxFor(node, userId));

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
        var userId = Guid.NewGuid();
        var mgr = new Mock<INftManager>();
        mgr.Setup(m => m.BurnAsync(nftId, walletId, userId, It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var handler = new NftBurnNodeHandler(mgr.Object);
        var cfg = new NftBurnNodeConfig { NftId = nftId, WalletId = walletId };
        var node = NodeWith(ActionNodeType.NftBurn, JsonSerializer.Serialize(cfg));
        var result = await handler.HandleAsync(CtxFor(node, userId));

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.BurnAsync(nftId, walletId, userId, It.IsAny<AZOARequest?>()), Times.Once);
    }

    // ─── Wallet group (IWalletManager) ───

    [Fact]
    public async Task WalletSetDefaultNodeHandler_InvokesSetDefault_WithActionUserAndConfigWallet()
    {
        var userId = Guid.NewGuid();
        var walletId = Guid.NewGuid();
        var mgr = new Mock<IWalletManager>();
        mgr.Setup(m => m.SetDefaultAsync(userId, walletId, It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<bool> { Result = true });

        var handler = new WalletSetDefaultNodeHandler(mgr.Object);
        handler.NodeType.Should().Be(ActionNodeType.WalletSetDefault);

        var node = NodeWith(ActionNodeType.WalletSetDefault,
            JsonSerializer.Serialize(new WalletSetDefaultNodeConfig { WalletId = walletId }));
        var result = await handler.HandleAsync(CtxFor(node, userId));

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.SetDefaultAsync(userId, walletId, It.IsAny<AZOARequest?>()), Times.Once);
    }

    [Fact]
    public async Task WalletGetNodeHandler_ManagerError_MapsToFailed()
    {
        var mgr = new Mock<IWalletManager>();
        mgr.Setup(m => m.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<IWallet> { IsError = true, Message = "nope" });

        var handler = new WalletGetNodeHandler(mgr.Object);
        var node = NodeWith(ActionNodeType.WalletGet, JsonSerializer.Serialize(new IdConfig { Id = Guid.NewGuid() }));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("nope");
    }

    // ─── action graph bundle group (IActionGraphBundleManager) ───

    [Fact]
    public async Task ActionGraphBundleDeployNodeHandler_Preserves_pending_domain_identity()
    {
        var target = new DeploymentTarget("NativeContract", "Ethereum", "Devnet");
        var request = new DeploymentStartRequest("artifact", "hash", target);
        var mgr = new Mock<IDeploymentService>();
        mgr.Setup(m => m.StartActionAsync(It.IsAny<ActionExternalOperationReference>(), It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
           .ReturnsAsync((ActionExternalOperationReference reference, string _, bool _, CancellationToken _) =>
               AZOAResult<DeploymentReceipt>.Success(new(reference.OperationId!, "artifact", "hash", target, "Submitted", "Execute", null, null, ["transaction"], null, 1)));

        var handler = new ActionGraphBundleDeployNodeHandler(mgr.Object);
        handler.NodeType.Should().Be(ActionNodeType.ActionGraphBundleDeploy);

        var node = NodeWith(ActionNodeType.ActionGraphBundleDeploy, JsonSerializer.Serialize(new DeploymentNodeConfig { Deployment = request }));
        var context = CtxFor(node, Guid.NewGuid());
        var result = await handler.HandleAsync(context);

        result.IsError.Should().BeFalse();
        result.PendingOperation.Should().NotBeNull();
        result.PendingOperation!.Status.Should().Be(ActionExternalOperationStatus.Pending);
        result.PendingOperation.Reference.ActorUserId.Should().Be(context.ActingUserId);
        result.PendingOperation.Reference.RequestKey.Should().Be($"action:{context.RunId:N}:{context.NodeId:N}:deployment");
        mgr.Verify(m => m.StartActionAsync(It.Is<ActionExternalOperationReference>(r => r.ActorUserId == context.ActingUserId
            && r.RequestKey == result.PendingOperation.Reference.RequestKey), It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ActionGraphBundleGenerateNodeHandler_InvokesGenerate_WithConfiguredActionGraphBundleId()
    {
        var bundleId = Guid.NewGuid();
        var target = new DeploymentTarget("PlatformRuntime", "Algorand", "Devnet");
        var mgr = new Mock<IDeploymentService>();
        mgr.Setup(m => m.GenerateAsync(bundleId, It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<DeploymentGenerateRequest>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(AZOAResult<DeploymentArtifactView>.Success(new("artifact", "payload", "hash", target, "export async function invoke() {}")));

        var handler = new ActionGraphBundleGenerateNodeHandler(mgr.Object);
        var cfg = new DeploymentGenerationNodeConfig { BundleId = bundleId, Request = new DeploymentGenerateRequest(target) };
        var node = NodeWith(ActionNodeType.ActionGraphBundleGenerate, JsonSerializer.Serialize(cfg));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.GenerateAsync(bundleId, It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<DeploymentGenerateRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Search (ISearchManager) ───

    [Fact]
    public async Task SearchNodeHandler_InvokesSearch_AndMapsSuccess()
    {
        var mgr = new Mock<ISearchManager>();
        mgr.Setup(m => m.SearchAsync(It.IsAny<SearchRequest>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<SearchResult> { Result = new SearchResult() });

        var handler = new SearchNodeHandler(mgr.Object);
        handler.NodeType.Should().Be(ActionNodeType.Search);

        var node = NodeWith(ActionNodeType.Search, JsonSerializer.Serialize(new SearchRequest()));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.SearchAsync(It.IsAny<SearchRequest>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()), Times.Once);
    }

    // ─── User NFT (IUserNFTService) ───

    [Fact]
    public async Task UserNFTGetCompositeNodeHandler_InvokesComposite_WithConfiguredId()
    {
        var id = Guid.NewGuid();
        var svc = new Mock<IUserNFTService>();
        svc.Setup(s => s.GetUserNFTCompositeAsync(id, It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<UserNFTCompositeResult> { Result = new UserNFTCompositeResult() });

        var handler = new UserNFTGetCompositeNodeHandler(svc.Object);
        handler.NodeType.Should().Be(ActionNodeType.UserNFTGetComposite);

        var node = NodeWith(ActionNodeType.UserNFTGetComposite, JsonSerializer.Serialize(new IdConfig { Id = id }));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeFalse();
        svc.Verify(s => s.GetUserNFTCompositeAsync(id, It.IsAny<AZOARequest?>()), Times.Once);
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
        handler.NodeType.Should().Be(ActionNodeType.BlockchainExecute);

        var node = NodeWith(ActionNodeType.BlockchainExecute, JsonSerializer.Serialize(new IdConfig { Id = id }));
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeFalse();
        mgr.Verify(m => m.GetAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()), Times.Once);
    }

    // ─── Control-flow (no manager dependency) ───

    [Fact]
    public async Task ConditionNodeHandler_PassesThroughConfigAsOutput()
    {
        var handler = new ConditionNodeHandler();
        handler.NodeType.Should().Be(ActionNodeType.Condition);

        var node = NodeWith(ActionNodeType.Condition, "{\"branch\":true}");
        var result = await handler.HandleAsync(CtxFor(node, Guid.NewGuid()));

        result.IsError.Should().BeFalse();
        result.Output.Should().Be("{\"branch\":true}");
    }

    [Fact]
    public async Task ComposeOutputsNodeHandler_GathersUpstreamOutputs()
    {
        var handler = new ComposeOutputsNodeHandler();
        handler.NodeType.Should().Be(ActionNodeType.ComposeOutputs);

        // Upstream and target nodes live on the definition; per-run outputs
        // live on ActionNodeExecution and are passed via context.UpstreamExecutions.
        var upstreamNode = new ActionNode { Id = Guid.NewGuid(), Name = "up" };
        var node = new ActionNode { Id = Guid.NewGuid(), NodeType = ActionNodeType.ComposeOutputs, Config = "{}" };
        var action = new ActionEntity
        {
            Id = Guid.NewGuid(),
            Nodes = new List<ActionNode> { upstreamNode, node },
            Edges = new List<ActionEdge>
            {
                new() { Id = Guid.NewGuid(), SourceNodeId = upstreamNode.Id, TargetNodeId = node.Id }
            }
        };

        var runId = Guid.NewGuid();
        var upstreamExec = new ActionNodeExecution
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            NodeId = upstreamNode.Id,
            State = ActionNodeState.Succeeded,
            Output = "\"value\""
        };
        var ctx = new ActionNodeExecutionContext(runId, node.Id, action, action.UserId,
            new Dictionary<Guid, ActionNodeExecution> { [upstreamNode.Id] = upstreamExec });

        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeFalse();
        var composed = JsonSerializer.Deserialize<Dictionary<string, string>>(result.Output!, ActionNodeJson.Options)!;
        composed.Should().ContainKey("up");
        composed["up"].Should().Be("\"value\"");
    }
}
