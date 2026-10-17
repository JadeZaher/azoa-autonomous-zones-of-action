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

/// <summary>
/// The durable-workflow-engine headline acceptance proof. It drives the REAL
/// <see cref="SagaProcessor"/> + the REAL <see cref="ActionNodeStepHandler"/> /
/// <see cref="ActionCompensateStepHandler"/> over the in-memory
/// <see cref="InMemorySagaStore"/> + the in-memory action stores, with
/// mechanism-only node handlers (no real value movement). It demonstrates the
/// engine's four core guarantees end to end:
///
/// <list type="number">
/// <item>an auto DAG runs to completion;</item>
/// <item>a gated run SUSPENDS, survives a full processor+scope RESTART (durability
/// lives in the saga rows, not memory), then RESUMES on signal;</item>
/// <item>a failing node exhausts retries and routes to COMPENSATION;</item>
/// <item>a manual node SUSPENDS until an explicit <c>advance()</c> (the
/// <c>step()</c> primitive).</item>
/// </list>
///
/// <para>A fifth case asserts a gated node resumes to its single Control
/// successor (branch-by-signal-selection; fork-merge fan-out is out of scope).</para>
/// </summary>
public sealed class DurableWorkflowEngineTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    // ── Engine harness ─────────────────────────────────────────────────────────

    /// <summary>
    /// Holds the durable substrate (the saga store + the three action stores) that
    /// OUTLIVES any single processor instance, plus the live node handlers so a
    /// test can assert what ran. A processor + its DI scope is built on demand and
    /// can be discarded/rebuilt over the SAME substrate to model a crash/restart.
    /// </summary>
    private sealed class Harness
    {
        public InMemorySagaStore SagaStore { get; } = new();
        public InMemoryActionStore ActionStore { get; } = new();
        public InMemoryActionRunStore RunStore { get; } = new();
        public InMemoryActionNodeExecutionStore ExecutionStore { get; } = new();
        public required IActionNodeHandler NodeHandler { get; init; }

        /// <summary>
        /// The wallet manager the chain-capability gate (D1) resolves
        /// "wallet bound" from. Defaults to "no wallet bound" (empty) so the
        /// existing engine tests — which drive Tier-1 nodes — are unaffected;
        /// a capability test overrides it to a bound wallet.
        /// </summary>
        public IWalletManager WalletManager { get; init; } = WalletManagerMocks.Empty();

        /// <summary>
        /// The provider factory the reconcile-before-retry re-probe resolves chain
        /// truth from. Defaults to the conservative <see cref="ChainConfirmation.Unknown"/>
        /// (parks); a reconcile test overrides it to a definite verdict.
        /// </summary>
        public IBlockchainProviderFactory ProviderFactory { get; init; } =
            BlockchainProviderFactoryFakes.Returning();

        private InMemoryActionWorkflowAdmissionStore? _admission;
        private InMemoryActionWorkflowAdmissionStore Admission => _admission ??= new(RunStore, ExecutionStore, SagaStore);

        public ActionManager NewManager() => new(
            ActionStore,
            RunStore,
            ExecutionStore,
            new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(new[] { NodeHandler }),
            SagaStore,
            WalletManager,
            ProviderFactory,
            BindingResolverFakes.PassThrough(), workflowAdmission: Admission);

        /// <summary>
        /// Build a FRESH <see cref="SagaProcessor"/> over a FRESH DI scope, all
        /// pointed at this harness's durable stores. Returns the processor and its
        /// owning <see cref="ServiceProvider"/> so a test can dispose it to model a
        /// process restart — the next processor reads the same rows.
        /// </summary>
        public (SagaProcessor Processor, ServiceProvider Scope) NewProcessor()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            services.AddSingleton<ISagaStore>(SagaStore);
            services.AddSingleton<IActionStore>(ActionStore);
            services.AddSingleton<IActionRunStore>(RunStore);
            services.AddSingleton<IActionNodeExecutionStore>(ExecutionStore);
            services.AddSingleton<IActionWorkflowAdmissionStore>(Admission);
            services.AddSingleton<IActionNodeHandlerRegistry>(
                new ActionNodeHandlerRegistry(new[] { NodeHandler }));
            services.AddSingleton<IWalletManager>(WalletManager);
            services.AddSingleton(ProviderFactory);
            services.AddSingleton(BindingResolverFakes.PassThrough());

            // The two typed step handlers the ActionWorkflow saga dispatches.
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

        /// <summary>
        /// Run the engine: loop <see cref="SagaProcessor.ProcessDueStepsAsync"/>
        /// until a tick processes nothing (the run is quiescent — completed,
        /// parked, or suspended) or <paramref name="maxTicks"/> is hit. Retry
        /// backoff is collapsed deterministically each tick via
        /// <see cref="InMemorySagaStore.PullForwardPendingRetries"/> so a failing
        /// node exhausts its budget without blocking on wall-clock time.
        /// </summary>
        public async Task PumpAsync(SagaProcessor processor, int maxTicks = 20)
        {
            for (var tick = 0; tick < maxTicks; tick++)
            {
                SagaStore.PullForwardPendingRetries();
                var processed = await processor.ProcessDueStepsAsync(CancellationToken.None);
                if (processed == 0)
                    return;
            }
        }

        public ActionRunStatus RunStatus(Guid runId) =>
            RunStore.GetByIdAsync(runId).GetAwaiter().GetResult().Result!.Status;
    }

    // ── Node handlers (mechanism only) ─────────────────────────────────────────

    /// <summary>A node handler that records each run and succeeds. The counter lets
    /// a test assert run-once semantics across the whole engine.</summary>
    private sealed class RecordingNodeHandler : IActionNodeHandler
    {
        private readonly List<Guid> _ran = new();
        public ActionNodeType NodeType { get; }
        public RecordingNodeHandler(ActionNodeType nodeType) => NodeType = nodeType;

        public IReadOnlyList<Guid> Ran
        {
            get { lock (_ran) return _ran.ToList(); }
        }
        public int CountFor(Guid nodeId)
        {
            lock (_ran) return _ran.Count(id => id == nodeId);
        }

        public Task<ActionNodeHandlerResult> HandleAsync(
            ActionNodeExecutionContext context, CancellationToken ct = default)
        {
            lock (_ran) _ran.Add(context.NodeId);
            return Task.FromResult(ActionNodeHandlerResult.Ok($"ran {context.NodeId}"));
        }
    }

    /// <summary>A node handler that ALWAYS fails — drives the retry→compensation
    /// path. Records every attempt so the test can prove the budget was consumed.</summary>
    private sealed class FailingNodeHandler : IActionNodeHandler
    {
        public ActionNodeType NodeType { get; }
        public int Attempts { get; private set; }
        public FailingNodeHandler(ActionNodeType nodeType) => NodeType = nodeType;

        public Task<ActionNodeHandlerResult> HandleAsync(
            ActionNodeExecutionContext context, CancellationToken ct = default)
        {
            Attempts++;
            return Task.FromResult(ActionNodeHandlerResult.Fail("intentional failure (test)"));
        }
    }

    // ── Action-definition builder ───────────────────────────────────────────────

    /// <summary>Build a linear action A→B→C… from node specs and persist it into the
    /// harness's action store. Each spec carries the node's <c>_workflow</c> marker
    /// (or null for auto). The first node is the entry, the last is terminal.</summary>
    private static ActionEntity BuildLinearAction(Harness h, ActionNodeType nodeType, params (string Name, string? Workflow)[] specs)
    {
        var actionId = Guid.NewGuid();
        var nodes = new List<ActionNode>();
        for (var i = 0; i < specs.Length; i++)
        {
            nodes.Add(new ActionNode
            {
                Id = Guid.NewGuid(),
                ActionId = actionId,
                Name = specs[i].Name,
                NodeType = nodeType,
                Config = specs[i].Workflow ?? "{}",
                IsEntry = i == 0,
                IsTerminal = i == specs.Length - 1,
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
            Id = actionId,
            Name = "WorkflowAction",
            UserId = UserId,
            Status = ActionStatus.Active,   // B6: must be Active to start a workflow run
            Nodes = nodes,
            Edges = edges,
        };
        h.ActionStore.UpsertActionAsync(action).GetAwaiter().GetResult();
        return action;
    }

    private static string Gated(string gateId) =>
        $"{{\"_workflow\":{{\"advance\":\"gated\",\"gateId\":\"{gateId}\"}}}}";

    private const string Manual = "{\"_workflow\":{\"advance\":\"manual\"}}";


    [Fact]
    public async Task AutoDag_RunsToCompletion()
    {
        var node = new RecordingNodeHandler(ActionNodeType.Condition);
        var h = new Harness { NodeHandler = node };
        var action = BuildLinearAction(h, ActionNodeType.Condition,
            ("A", null), ("B", null), ("C", null));

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(action.Id, UserId, new AZOARequest { IdempotencyKey = Guid.NewGuid().ToString("N") });
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor);
        scope.Dispose();

        foreach (var n in action.Nodes)
            node.CountFor(n.Id).Should().Be(1, $"node {n.Name} should run exactly once");
        h.RunStatus(runId).Should().Be(ActionRunStatus.Succeeded);
    }


    [Fact]
    public async Task GatedRun_SuspendsThenSurvivesRestart_ThenResumesOnSignal()
    {
        var node = new RecordingNodeHandler(ActionNodeType.Condition);
        var h = new Harness { NodeHandler = node };
        // A(auto) -> Gate(gated "phase-met") -> C(auto, terminal)
        var action = BuildLinearAction(h, ActionNodeType.Condition,
            ("A", null), ("Gate", Gated("phase-met")), ("C", null));
        var nodeA = action.Nodes[0];
        var nodeGate = action.Nodes[1];
        var nodeC = action.Nodes[2];

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(action.Id, UserId, new AZOARequest { IdempotencyKey = Guid.NewGuid().ToString("N") });
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        // First processor instance: pumps until the gate parks.
        var (processor1, scope1) = h.NewProcessor();
        await h.PumpAsync(processor1);

        h.RunStatus(runId).Should().Be(ActionRunStatus.AwaitingSignal,
            "the run must suspend at the gate");
        node.CountFor(nodeA.Id).Should().Be(1, "A ran before the gate");
        node.CountFor(nodeGate.Id).Should().Be(0, "the gate parks BEFORE doing its work");
        node.CountFor(nodeC.Id).Should().Be(0, "C is downstream of the parked gate");

        scope1.Dispose();

        var signal = await manager.SignalAsync(runId, "phase-met", "phase-met", UserId);
        signal.IsError.Should().BeFalse(signal.Message);

        var (processor2, scope2) = h.NewProcessor();
        await h.PumpAsync(processor2);
        scope2.Dispose();

        node.CountFor(nodeGate.Id).Should().Be(1, "the gate ran after the signal un-parked it");
        node.CountFor(nodeC.Id).Should().Be(1, "C ran after the gate resumed");
        h.RunStatus(runId).Should().Be(ActionRunStatus.Succeeded);
    }


    [Fact]
    public async Task FailingNode_ExhaustsRetries_RoutesToCompensation()
    {
        var failing = new FailingNodeHandler(ActionNodeType.Search);
        var h = new Harness { NodeHandler = failing };
        var action = BuildLinearAction(h, ActionNodeType.Search, ("Grant", null));
        var grant = action.Nodes[0];

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(action.Id, UserId, new AZOARequest { IdempotencyKey = Guid.NewGuid().ToString("N") });
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor, maxTicks: 40);
        scope.Dispose();

        failing.Attempts.Should().Be(1,
            "saga retries must replay the recorded failure without repeating the handler");

        var steps = h.SagaStore.Snapshot();
        var nodeStep = steps.FirstOrDefault(s => s.StepName == grant.Id.ToString());
        nodeStep.Should().NotBeNull();
        nodeStep!.Status.Should().Be(StepStatus.Compensating,
            "the forward node-step routes to compensation on exhaustion");

        var compStep = steps.FirstOrDefault(s => s.StepName == ActionWorkflowSaga.CompensateStepName);
        compStep.Should().NotBeNull("a compensation step must have been enqueued");
        compStep!.IsCompensation.Should().BeTrue();
        compStep.Status.Should().Be(StepStatus.Completed,
            "the compensation handler ran and settled the saga");

        h.RunStatus(runId).Should().Be(ActionRunStatus.Failed,
            "read-only failure closes without claiming a value reversal: " +
            "the compensation handler records the failed run");
    }


    [Fact]
    public async Task ManualNode_SuspendsUntilAdvance_ThenCompletes()
    {
        var node = new RecordingNodeHandler(ActionNodeType.Condition);
        var h = new Harness { NodeHandler = node };
        // A(auto) -> B(manual) -> C(auto, terminal)
        var action = BuildLinearAction(h, ActionNodeType.Condition,
            ("A", null), ("B", Manual), ("C", null));
        var nodeA = action.Nodes[0];
        var nodeB = action.Nodes[1];
        var nodeC = action.Nodes[2];

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(action.Id, UserId, new AZOARequest { IdempotencyKey = Guid.NewGuid().ToString("N") });
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        var (processor1, scope1) = h.NewProcessor();
        await h.PumpAsync(processor1);
        scope1.Dispose();

        h.RunStatus(runId).Should().Be(ActionRunStatus.Suspended);
        node.CountFor(nodeA.Id).Should().Be(1);
        node.CountFor(nodeB.Id).Should().Be(1, "the manual node runs, then suspends");
        node.CountFor(nodeC.Id).Should().Be(0, "C waits for an explicit advance()");

        // step(): advance from B into its single Control successor.
        var advance = await manager.AdvanceAsync(runId, nodeB.Id, UserId);
        advance.IsError.Should().BeFalse(advance.Message);

        var (processor2, scope2) = h.NewProcessor();
        await h.PumpAsync(processor2);
        scope2.Dispose();

        node.CountFor(nodeC.Id).Should().Be(1, "C runs after advance()");
        h.RunStatus(runId).Should().Be(ActionRunStatus.Succeeded);
    }


    [Fact]
    public async Task GatedNode_ResumesToSingleSuccessor_OnSignal()
    {
        var node = new RecordingNodeHandler(ActionNodeType.Condition);
        var h = new Harness { NodeHandler = node };
        var action = BuildLinearAction(h, ActionNodeType.Condition,
            ("Gate", Gated("decision")), ("Continue", null));
        var gate = action.Nodes[0];
        var cont = action.Nodes[1];

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(action.Id, UserId, new AZOARequest { IdempotencyKey = Guid.NewGuid().ToString("N") });
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        var (processor1, scope1) = h.NewProcessor();
        await h.PumpAsync(processor1);
        h.RunStatus(runId).Should().Be(ActionRunStatus.AwaitingSignal);
        node.CountFor(cont.Id).Should().Be(0, "the successor waits behind the gate");
        scope1.Dispose();

        var signal = await manager.SignalAsync(runId, "decision", "go", UserId);
        signal.IsError.Should().BeFalse(signal.Message);

        var (processor2, scope2) = h.NewProcessor();
        await h.PumpAsync(processor2);
        scope2.Dispose();

        node.CountFor(gate.Id).Should().Be(1, "the gate ran once it was signalled");
        node.CountFor(cont.Id).Should().Be(1, "the single successor ran after the gate resumed");
        h.RunStatus(runId).Should().Be(ActionRunStatus.Succeeded);
    }


    [Fact]
    public void ForwardPayloadJson_DeserializesIntoCompensationPayload_FieldsPreserved()
    {
        var forward = new ActionStepPayload(
            RunId: Guid.NewGuid(),
            ActionId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            NodeId: Guid.NewGuid(),
            SignalPayload: "phase-met");

        var forwardJson = SagaStep<ActionStepPayload>.Serialize(forward);
        var comp = System.Text.Json.JsonSerializer.Deserialize<ActionCompensatePayload>(forwardJson);

        comp.Should().NotBeNull();
        comp!.RunId.Should().Be(forward.RunId, "RunId must survive the type-pun round-trip");
        comp.ActionId.Should().Be(forward.ActionId, "ActionId must survive the type-pun round-trip");
        comp.UserId.Should().Be(forward.UserId, "UserId must survive the type-pun round-trip");

        forward.ToCompensation().Should().Be(comp);
    }
}
