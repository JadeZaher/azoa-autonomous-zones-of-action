using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Handlers;
using AZOA.WebAPI.Services.Actions.Workflow;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions.Workflow;

/// <summary>
/// T9 — the pre-execution value-rail capability gate (economic-primitive-nodes D1,
/// value-rail seam D9).
///
/// <para>Proves the locked rule at BOTH dispatch seams (the legacy synchronous
/// <see cref="ActionManager"/> executor and the durable
/// <see cref="ActionNodeStepHandler"/> saga step): a node whose handler declares
/// <see cref="IActionNodeHandler.RequiresChainCapability"/> <c>== true</c> is
/// rejected BEFORE <c>HandleAsync</c> — and therefore before any broadcast — when
/// the run's actor has no usable destination ON THE RAIL that node settles on. A
/// Tier-1 node (capability flag false) is never gated, so a pure-metadata action runs
/// end to end with no wallet and no chain call.</para>
/// </summary>
public sealed class ValueRailCapabilityGateTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    // ── Harness (mirrors DurableWorkflowEngineTests, parameterized on the gate) ──

    private sealed class Harness
    {
        public InMemorySagaStore SagaStore { get; } = new();
        public InMemoryActionStore ActionStore { get; } = new();
        public InMemoryActionRunStore RunStore { get; } = new();
        public InMemoryActionNodeExecutionStore ExecutionStore { get; } = new();
        public required IActionNodeHandler NodeHandler { get; init; }
        public IWalletManager WalletManager { get; init; } = WalletManagerMocks.Empty();

        public ActionManager NewManager() => new(
            ActionStore, RunStore, ExecutionStore,
            new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(new[] { NodeHandler }),
            SagaStore, WalletManager,
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        public (SagaProcessor Processor, ServiceProvider Scope) NewProcessor()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ISagaStore>(SagaStore);
            services.AddSingleton<IActionStore>(ActionStore);
            services.AddSingleton<IActionRunStore>(RunStore);
            services.AddSingleton<IActionNodeExecutionStore>(ExecutionStore);
            services.AddSingleton<IActionNodeHandlerRegistry>(
                new ActionNodeHandlerRegistry(new[] { NodeHandler }));
            services.AddSingleton<IWalletManager>(WalletManager);
            // ActionNodeStepHandler resolves a provider factory for reconcile-before-retry.
            services.AddSingleton(BlockchainProviderFactoryFakes.Returning());
            // ActionNodeStepHandler depends on the binding resolver (holon-path $from
            // resolution); register the pass-through fake so the scope can build it.
            services.AddSingleton(BindingResolverFakes.PassThrough());
            services.AddScoped<IStepHandler<ActionStepPayload>, ActionNodeStepHandler>();
            services.AddScoped<IStepHandler<ActionCompensatePayload>, ActionCompensateStepHandler>();

            var provider = services.BuildServiceProvider();
            var processor = new SagaProcessor(
                SagaStore,
                new SagaRegistry(new ISagaDefinition[] { new ActionWorkflowSagaDefinition() }),
                provider,
                provider.GetRequiredService<ILogger<SagaProcessor>>(),
                Options.Create(new SagaOptions()));
            return (processor, provider);
        }

        public async Task PumpAsync(SagaProcessor processor, int maxTicks = 20)
        {
            for (var tick = 0; tick < maxTicks; tick++)
            {
                SagaStore.PullForwardPendingRetries();
                if (await processor.ProcessDueStepsAsync(CancellationToken.None) == 0)
                    return;
            }
        }

        public ActionNodeExecution ExecutionFor(Guid runId, Guid nodeId) =>
            ExecutionStore.GetByRunAndNodeAsync(runId, nodeId).GetAwaiter().GetResult().Result!;
    }

    /// <summary>Tier-1 recording handler — capability flag false; never gated.</summary>
    private sealed class Tier1RecordingHandler : IActionNodeHandler
    {
        private readonly List<Guid> _ran = new();
        public ActionNodeType NodeType { get; }
        public Tier1RecordingHandler(ActionNodeType nodeType) => NodeType = nodeType;
        public bool RequiresChainCapability => false;
        public int CountFor(Guid nodeId) { lock (_ran) return _ran.Count(id => id == nodeId); }

        public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
        {
            lock (_ran) _ran.Add(context.NodeId);
            return Task.FromResult(ActionNodeHandlerResult.Ok($"ran {context.NodeId}"));
        }
    }

    // ── ActionDefinition-definition builder (single linear chain of one node type) ──────────

    private static ActionEntity BuildLinearAction(Harness h, ActionNodeType nodeType, params string[] names)
    {
        var actionId = Guid.NewGuid();
        var nodes = new List<ActionNode>();
        for (var i = 0; i < names.Length; i++)
        {
            nodes.Add(new ActionNode
            {
                Id = Guid.NewGuid(),
                ActionId = actionId,
                Name = names[i],
                NodeType = nodeType,
                Config = TransferConfigJson(),
                ExecutionOrder = i,
                IsEntry = i == 0,
                IsTerminal = i == names.Length - 1,
            });
        }
        var edges = new List<ActionEdge>();
        for (var i = 0; i < nodes.Count - 1; i++)
        {
            edges.Add(new ActionEdge
            {
                Id = Guid.NewGuid(),
                ActionId = actionId,
                SourceNodeId = nodes[i].Id,
                TargetNodeId = nodes[i + 1].Id,
                EdgeType = ActionEdgeType.Control,
            });
        }
        var action = new ActionEntity
        {
            Id = actionId, Name = "GateAction", UserId = UserId,
            Status = ActionStatus.Active,   // B6: must be Active to execute
            Nodes = nodes, Edges = edges,
        };
        h.ActionStore.UpsertActionAsync(action).GetAwaiter().GetResult();
        return action;
    }

    // A valid TransferNodeConfig body so the real TransferNodeHandler can
    // deserialize IF the gate ever lets it through (the reject test asserts it
    // never does). Tier-1 handlers ignore Config, so this is harmless for them.
    private static string TransferConfigJson() =>
        $"{{\"NftId\":\"{Guid.NewGuid()}\",\"Request\":{{\"TargetUserId\":\"{Guid.NewGuid()}\",\"WalletId\":\"{Guid.NewGuid()}\"}}}}";

    private static (TransferNodeHandler Handler, Mock<INftManager> Nft) NewTransferHandler(bool succeed = true)
    {
        var nft = new Mock<INftManager>();
        nft.Setup(m => m.TransferAsync(
                It.IsAny<Guid>(), It.IsAny<NftTransferRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
            .ReturnsAsync(new AZOAResult<IBlockchainOperation> { IsError = !succeed, Result = null });
        return (new TransferNodeHandler(nft.Object), nft);
    }

    // ════════════════════════════════════════════════════════════════════════
    // CASE 1 (durable seam) — Tier-2 rejected pre-execution when no wallet bound
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DurableSeam_Tier2_NoWalletBound_FailsClosed_NoBroadcast()
    {
        var (handler, nft) = NewTransferHandler();
        var h = new Harness { NodeHandler = handler, WalletManager = WalletManagerMocks.Empty() };
        var action = BuildLinearAction(h, ActionNodeType.Transfer, "Transfer");
        var transferNode = action.Nodes[0];

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(action.Id, UserId);
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor);
        scope.Dispose();

        var exec = h.ExecutionFor(runId, transferNode.Id);
        exec.State.Should().Be(ActionNodeState.Failed, "the gate must reject a Tier-2 node with no wallet bound");
        exec.Error.Should().Be(ValueRailCapabilityGate.NoDestinationMessage("Algorand"));

        nft.Verify(m => m.TransferAsync(
                It.IsAny<Guid>(), It.IsAny<NftTransferRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Times.Never, "HandleAsync must be SKIPPED — no chain broadcast on a gated node");
    }

    // ════════════════════════════════════════════════════════════════════════
    // CASE 1b (legacy seam) — same rejection through ActionManager.ExecuteAsync
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LegacySeam_Tier2_NoWalletBound_FailsClosed_NoBroadcast()
    {
        var (handler, nft) = NewTransferHandler();
        var h = new Harness { NodeHandler = handler, WalletManager = WalletManagerMocks.Empty() };
        var action = BuildLinearAction(h, ActionNodeType.Transfer, "Transfer");
        var transferNode = action.Nodes[0];

        var manager = h.NewManager();
        var run = await manager.ExecuteAsync(action.Id, UserId);
        run.IsError.Should().BeFalse(run.Message);

        var exec = h.ExecutionFor(run.Result!.Id, transferNode.Id);
        exec.State.Should().Be(ActionNodeState.Failed);
        exec.Error.Should().Be(ValueRailCapabilityGate.NoDestinationMessage("Algorand"));

        nft.Verify(m => m.TransferAsync(
                It.IsAny<Guid>(), It.IsAny<NftTransferRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Times.Never);
    }

    // ════════════════════════════════════════════════════════════════════════
    // CASE 2 — Pure-metadata action runs end-to-end with NO wallet, NO chain call
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PureMetadataAction_NoWallet_RunsEndToEnd_NoChainCall()
    {
        // A Tier-1-only DAG (capability flag false everywhere). The wallet
        // manager is empty AND its QueryAsync must NEVER be consulted, because
        // the gate is not even reached for a Tier-1 node.
        var walletMock = new Mock<IWalletManager>();
        walletMock.Setup(m => m.QueryAsync(
                It.IsAny<WalletQueryRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { IsError = false, Result = Array.Empty<IWallet>() });

        var node = new Tier1RecordingHandler(ActionNodeType.Condition);
        var h = new Harness { NodeHandler = node, WalletManager = walletMock.Object };
        var action = BuildLinearAction(h, ActionNodeType.Condition, "A", "B", "C");

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(action.Id, UserId);
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor);
        scope.Dispose();

        foreach (var n in action.Nodes)
            node.CountFor(n.Id).Should().Be(1, $"Tier-1 node {n.Name} runs unconditionally");
        h.RunStore.GetByIdAsync(runId).GetAwaiter().GetResult().Result!
            .Status.Should().Be(ActionRunStatus.Succeeded);

        // The gate is never triggered for Tier-1 ⇒ the wallet manager is never
        // even queried (no capability resolution, no chain manager touched).
        walletMock.Verify(m => m.QueryAsync(
                It.IsAny<WalletQueryRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()),
            Times.Never, "a pure-metadata action never resolves wallet-bound capability");
    }

    // ════════════════════════════════════════════════════════════════════════
    // CASE 3 — Tier-2 ALLOWED when a wallet IS bound (gate passes → HandleAsync)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DurableSeam_Tier2_WalletBound_GatePasses_HandleInvoked()
    {
        var (handler, nft) = NewTransferHandler(succeed: true);
        var h = new Harness { NodeHandler = handler, WalletManager = WalletManagerMocks.WithOneWallet() };
        var action = BuildLinearAction(h, ActionNodeType.Transfer, "Transfer");
        var transferNode = action.Nodes[0];

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(action.Id, UserId);
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor);
        scope.Dispose();

        var exec = h.ExecutionFor(runId, transferNode.Id);
        exec.State.Should().Be(ActionNodeState.Succeeded, "the gate passes when a wallet is bound");

        nft.Verify(m => m.TransferAsync(
                It.IsAny<Guid>(), It.IsAny<NftTransferRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Times.Once, "HandleAsync ran and invoked the chain manager exactly once");
    }
}
