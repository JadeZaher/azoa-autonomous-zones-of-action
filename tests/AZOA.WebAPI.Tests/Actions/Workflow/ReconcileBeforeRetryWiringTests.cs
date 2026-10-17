using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Workflow;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions.Workflow;

/// <summary>Exercises original-operation evidence recovery without repeating effect dispatch.</summary>
public sealed class ReconcileBeforeRetryWiringTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private sealed class ChainActionNodeDouble(ActionNodeType nodeType, bool fail, string? txHash)
        : IRecoverableActionNodeHandler, IActionExternalOperationResolver
    {
        public ActionNodeType NodeType => nodeType;
        public bool RequiresChainCapability => true;
        public string Domain => "fixture-chain";
        public int DispatchCount { get; private set; }
        public Func<ChainConfirmation> Evidence { get; set; } = () => ChainConfirmation.Unknown;
        public Func<ActionNodeExecutionContext, ActionExternalOperationReference> SavedReference { get; set; } = null!;

        public ActionExternalOperationReference CreateReference(ActionNodeExecutionContext context, string hash)
            => new(1, Domain, $"fixture:{context.RunId:N}:{context.NodeId:N}", context.NodeId.ToString("N"), hash,
                context.ActingUserId, context.ActingTenantId);

        public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
        {
            DispatchCount++;
            if (!fail) return Task.FromResult(ActionNodeHandlerResult.Ok("{}", txHash: txHash, chainType: "Algorand"));
            return Task.FromResult(ActionNodeHandlerResult.Pending(Observe(SavedReference(context))));
        }

        public Task<ActionExternalOperationObservation> ObserveAsync(ActionExternalOperationReference reference,
            string canonicalInput, CancellationToken ct = default)
        {
            var input = JsonSerializer.Deserialize<ActionFrozenNodeInput>(canonicalInput)!;
            if (reference.IntentHash != CanonicalJson.HashCanonical(canonicalInput)
                || reference.ActorUserId != input.ActorUserId || reference.OperationId != input.NodeId.ToString("N"))
                throw new InvalidOperationException("Fixture receipt identity changed.");
            return Task.FromResult(Observe(reference));
        }

        private ActionExternalOperationObservation Observe(ActionExternalOperationReference reference)
            => Evidence() switch
            {
                ChainConfirmation.Confirmed => new(ActionExternalOperationStatus.Succeeded, reference, "{}", TransactionId: txHash, ChainType: "Algorand"),
                ChainConfirmation.FailedOnChain => new(ActionExternalOperationStatus.Failed, reference, Detail: "Exact original effect failed.", TransactionId: txHash, ChainType: "Algorand"),
                _ => new(ActionExternalOperationStatus.Pending, reference, Detail: "Original effect remains pending.", TransactionId: txHash, ChainType: "Algorand"),
            };
    }
    private sealed class Harness
    {
        public InMemorySagaStore SagaStore { get; } = new();
        public InMemoryActionStore ActionStore { get; } = new();
        public InMemoryActionRunStore RunStore { get; } = new();
        public InMemoryActionNodeExecutionStore ExecutionStore { get; } = new();
        public required ChainActionNodeDouble NodeHandler { get; init; }

        public IWalletManager WalletManager { get; } = WalletManagerMocks.WithOneWallet();

        public IBlockchainProviderFactory ProviderFactory { get; set; } =
            BlockchainProviderFactoryFakes.Returning(ChainConfirmation.Unknown);

        private InMemoryActionWorkflowAdmissionStore? _admission;
        private InMemoryActionWorkflowAdmissionStore Admission => _admission ??= new(RunStore, ExecutionStore, SagaStore);

        public ChainConfirmation Evidence { get; set; } = ChainConfirmation.Unknown;
        private IActionExternalOperationResolver[] Resolvers { get { NodeHandler.Evidence = () => Evidence; NodeHandler.SavedReference = context => ExecutionStore.GetByRunAndNodeAsync(context.RunId, context.NodeId).GetAwaiter().GetResult().Result!.ExternalOperation!; return [NodeHandler]; } }
        public ActionManager NewManager() => new(
            ActionStore, RunStore, ExecutionStore, new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(new IActionNodeHandler[] { NodeHandler }),
            SagaStore, WalletManager, ProviderFactory,
            BindingResolverFakes.PassThrough(), workflowAdmission: Admission, operationResolvers: Resolvers);

        public (SagaProcessor Processor, ServiceProvider Scope) NewProcessor()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IActionExternalOperationResolver>(Resolvers[0]);
            services.AddSingleton<ISagaStore>(SagaStore);
            services.AddSingleton<IActionStore>(ActionStore);
            services.AddSingleton<IActionRunStore>(RunStore);
            services.AddSingleton<IActionNodeExecutionStore>(ExecutionStore);
            services.AddSingleton<IActionWorkflowAdmissionStore>(Admission);
            services.AddSingleton<IActionNodeHandlerRegistry>(
                new ActionNodeHandlerRegistry(new IActionNodeHandler[] { NodeHandler }));
            services.AddSingleton<IWalletManager>(WalletManager);
            services.AddSingleton(ProviderFactory);
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

        public async Task PumpAsync(SagaProcessor processor, int maxTicks = 40)
        {
            for (var tick = 0; tick < maxTicks; tick++)
            {
                SagaStore.PullForwardPendingRetries();
                var processed = await processor.ProcessDueStepsAsync(CancellationToken.None);
                if (processed == 0) return;
            }
        }

        public ActionRunStatus RunStatus(Guid runId) =>
            RunStore.GetByIdAsync(runId).GetAwaiter().GetResult().Result!.Status;

        public ActionNodeExecution Execution(Guid runId, Guid nodeId) =>
            ExecutionStore.GetByRunAndNodeAsync(runId, nodeId).GetAwaiter().GetResult().Result!;
    }

    /// <summary>A single-node action whose only node is the chain-action node
    /// (entry + terminal). Keeps the DAG minimal so the assertion is purely about
    /// the reconcile branch, not graph topology.</summary>
    private static ActionEntity BuildSingleChainNodeAction(Harness h)
    {
        var actionId = Guid.NewGuid();
        var node = new ActionNode
        {
            Id = Guid.NewGuid(),
            ActionId = actionId,
            Name = "Grant",
            NodeType = h.NodeHandler.NodeType,
            Config = "{}",
            IsEntry = true,
            IsTerminal = true,
        };
        var action = new ActionEntity
        {
            Id = actionId,
            Name = "ReconcileAction",
            UserId = UserId,
            Status = ActionStatus.Active,
            Nodes = new List<ActionNode> { node },
            Edges = new List<ActionEdge>(),
        };
        h.ActionStore.UpsertActionAsync(action).GetAwaiter().GetResult();
        return action;
    }


    [Fact]
    public async Task ChainNode_FailedButTxConfirmed_AdvancesReconciled_DoesNotReMint()
    {
        var node = new ChainActionNodeDouble(ActionNodeType.Grant, fail: true, txHash: "TX_LANDED");
        var h = new Harness
        {
            NodeHandler = node,
            Evidence = ChainConfirmation.Confirmed,
        };
        var action = BuildSingleChainNodeAction(h);
        var grant = action.Nodes[0];

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(action.Id, UserId, new AZOARequest { IdempotencyKey = Guid.NewGuid().ToString("N") });
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor);
        scope.Dispose();

        node.DispatchCount.Should().Be(1,
            "a confirmed tx must NEVER be re-broadcast — the node runs exactly once");
        var exec = h.Execution(runId, grant.Id);
        exec.State.Should().Be(ActionNodeState.Succeeded,
            "the failed-but-confirmed node reconciles to success");
        exec.TxHash.Should().Be("TX_LANDED", "the landed tx hash is recorded on the reconciled row");
        h.RunStatus(runId).Should().Be(ActionRunStatus.Succeeded);
    }


    [Fact]
    public async Task ChainNode_TxFailedOnChain_RoutesToRetryThenCompensation()
    {
        var node = new ChainActionNodeDouble(ActionNodeType.Grant, fail: true, txHash: "TX_REVERTED");
        var h = new Harness
        {
            NodeHandler = node,
            Evidence = ChainConfirmation.FailedOnChain,
        };
        var action = BuildSingleChainNodeAction(h);

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(action.Id, UserId, new AZOARequest { IdempotencyKey = Guid.NewGuid().ToString("N") });
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor, maxTicks: 60);
        scope.Dispose();

        h.Execution(runId, action.Nodes[0].Id).State.Should().Be(ActionNodeState.Failed);
        node.DispatchCount.Should().Be(1, "a failed original effect is not permission to repeat the handler");
        h.RunStatus(runId).Should().Be(ActionRunStatus.AwaitingReconciliation);
    }


    [Fact]
    public async Task ChainNode_TxIndeterminate_ParksForReconciliation_DoesNotReMint()
    {
        var node = new ChainActionNodeDouble(ActionNodeType.Grant, fail: true, txHash: "TX_IN_LIMBO");
        var h = new Harness
        {
            NodeHandler = node,
            Evidence = ChainConfirmation.Unknown,
        };
        var action = BuildSingleChainNodeAction(h);
        var grant = action.Nodes[0];

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(action.Id, UserId, new AZOARequest { IdempotencyKey = Guid.NewGuid().ToString("N") });
        var runId = start.Result!.Id;

        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor);
        scope.Dispose();

        node.DispatchCount.Should().Be(1,
            "a parked node must NOT be re-dispatched — re-broadcast is the double-spend this prevents");
        h.RunStatus(runId).Should().Be(ActionRunStatus.AwaitingReconciliation,
            "an indeterminate verdict parks the run, never retries");
        var exec = h.Execution(runId, grant.Id);
        exec.State.Should().Be(ActionNodeState.Running);
        exec.TxHash.Should().Be("TX_IN_LIMBO",
            "the hash is stamped on the parked row so a later sweep can re-probe it");
    }


    [Fact]
    public async Task ReconcileSweep_OnParkedRun_ConfirmsTx_ResumesWithoutReMint()
    {
        var node = new ChainActionNodeDouble(ActionNodeType.Grant, fail: true, txHash: "TX_SETTLES_LATE");
        var h = new Harness
        {
            NodeHandler = node,
            Evidence = ChainConfirmation.Unknown,
        };
        var action = BuildSingleChainNodeAction(h);
        var grant = action.Nodes[0];

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(action.Id, UserId, new AZOARequest { IdempotencyKey = Guid.NewGuid().ToString("N") });
        var runId = start.Result!.Id;

        // Phase 1 — park.
        var (p1, s1) = h.NewProcessor();
        await h.PumpAsync(p1);
        s1.Dispose();
        h.RunStatus(runId).Should().Be(ActionRunStatus.AwaitingReconciliation);
        node.DispatchCount.Should().Be(1);

        h.Evidence = ChainConfirmation.Confirmed;
        var sweepManager = h.NewManager();

        var sweep = await sweepManager.SweepReconciliationAsync();
        sweep.IsError.Should().BeFalse(sweep.Message);
        sweep.Result!.Should().ContainSingle()
            .Which.ReconciledConfirmed.Should().Be(1, "the parked node's tx confirmed");

        var (p2, s2) = h.NewProcessor();
        await h.PumpAsync(p2);
        s2.Dispose();

        node.DispatchCount.Should().Be(1,
            "reconciliation must resume from chain truth, NEVER re-broadcast the node");
        h.Execution(runId, grant.Id).State.Should().Be(ActionNodeState.Succeeded);
        h.RunStatus(runId).Should().Be(ActionRunStatus.Succeeded);
    }

    [Fact]
    public async Task ReconcileRun_RejectsRunNotAwaitingReconciliation()
    {
        var node = new ChainActionNodeDouble(ActionNodeType.Grant, fail: false, txHash: "TX");
        var h = new Harness { NodeHandler = node };
        var action = BuildSingleChainNodeAction(h);

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(action.Id, UserId, new AZOARequest { IdempotencyKey = Guid.NewGuid().ToString("N") });
        var runId = start.Result!.Id;

        var reconcile = await manager.ReconcileRunAsync(runId, UserId);
        reconcile.IsError.Should().BeTrue(
            "only AwaitingReconciliation runs accept a manual re-probe");
        reconcile.Message.Should().Contain("AwaitingReconciliation");
    }
}
