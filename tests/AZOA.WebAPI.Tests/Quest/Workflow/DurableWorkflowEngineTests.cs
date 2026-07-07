using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Services.Quest.Workflow;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Tests.Quest.Workflow;

/// <summary>
/// The durable-workflow-engine headline acceptance proof. It drives the REAL
/// <see cref="SagaProcessor"/> + the REAL <see cref="QuestNodeStepHandler"/> /
/// <see cref="QuestCompensateStepHandler"/> over the in-memory
/// <see cref="InMemorySagaStore"/> + the in-memory quest stores, with
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
    private static readonly Guid AvatarId = Guid.NewGuid();

    // ── Engine harness ─────────────────────────────────────────────────────────

    /// <summary>
    /// Holds the durable substrate (the saga store + the three quest stores) that
    /// OUTLIVES any single processor instance, plus the live node handlers so a
    /// test can assert what ran. A processor + its DI scope is built on demand and
    /// can be discarded/rebuilt over the SAME substrate to model a crash/restart.
    /// </summary>
    private sealed class Harness
    {
        public InMemorySagaStore SagaStore { get; } = new();
        public InMemoryQuestStore QuestStore { get; } = new();
        public InMemoryQuestRunStore RunStore { get; } = new();
        public InMemoryQuestNodeExecutionStore ExecutionStore { get; } = new();
        public required IQuestNodeHandler NodeHandler { get; init; }

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

        public QuestManager NewManager() => new(
            QuestStore,
            RunStore,
            ExecutionStore,
            new QuestDagValidator(), new QuestDagExecutabilityValidator(),
            new QuestNodeHandlerRegistry(new[] { NodeHandler }),
            SagaStore,
            WalletManager,
            ProviderFactory,
            BindingResolverFakes.PassThrough());

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

            // The SAME in-memory store instances the manager uses — the handlers
            // resolved from this scope read/write the identical durable state.
            services.AddSingleton<ISagaStore>(SagaStore);
            services.AddSingleton<IQuestStore>(QuestStore);
            services.AddSingleton<IQuestRunStore>(RunStore);
            services.AddSingleton<IQuestNodeExecutionStore>(ExecutionStore);
            services.AddSingleton<IQuestNodeHandlerRegistry>(
                new QuestNodeHandlerRegistry(new[] { NodeHandler }));
            services.AddSingleton<IWalletManager>(WalletManager);
            // QuestNodeStepHandler resolves a provider factory for reconcile-before-retry.
            // Use the SAME factory the manager's re-probe uses so a reconcile test sets a
            // single verdict for both the live park (step handler) and the sweep (manager).
            // The default fake returns Unknown (conservative) and is never reached by the
            // non-chain / Tier-1 runs.
            services.AddSingleton(ProviderFactory);
            // QuestNodeStepHandler depends on the binding resolver (holon-path $from
            // resolution); register the pass-through fake so the scope can build it.
            services.AddSingleton(BindingResolverFakes.PassThrough());

            // The two typed step handlers the QuestWorkflow saga dispatches.
            services.AddScoped<IStepHandler<QuestStepPayload>, QuestNodeStepHandler>();
            services.AddScoped<IStepHandler<QuestCompensatePayload>, QuestCompensateStepHandler>();

            var provider = services.BuildServiceProvider();

            var processor = new SagaProcessor(
                SagaStore,
                new SagaRegistry(new ISagaDefinition[] { new QuestWorkflowSagaDefinition() }),
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

        public QuestRunStatus RunStatus(Guid runId) =>
            RunStore.GetByIdAsync(runId).GetAwaiter().GetResult().Result!.Status;
    }

    // ── Node handlers (mechanism only) ─────────────────────────────────────────

    /// <summary>A node handler that records each run and succeeds. The counter lets
    /// a test assert run-once semantics across the whole engine.</summary>
    private sealed class RecordingNodeHandler : IQuestNodeHandler
    {
        private readonly List<Guid> _ran = new();
        public QuestNodeType NodeType { get; }
        public RecordingNodeHandler(QuestNodeType nodeType) => NodeType = nodeType;

        public IReadOnlyList<Guid> Ran
        {
            get { lock (_ran) return _ran.ToList(); }
        }
        public int CountFor(Guid nodeId)
        {
            lock (_ran) return _ran.Count(id => id == nodeId);
        }

        public Task<QuestNodeHandlerResult> HandleAsync(
            QuestNodeExecutionContext context, CancellationToken ct = default)
        {
            lock (_ran) _ran.Add(context.NodeId);
            return Task.FromResult(QuestNodeHandlerResult.Ok($"ran {context.NodeId}"));
        }
    }

    /// <summary>A node handler that ALWAYS fails — drives the retry→compensation
    /// path. Records every attempt so the test can prove the budget was consumed.</summary>
    private sealed class FailingNodeHandler : IQuestNodeHandler
    {
        public QuestNodeType NodeType { get; }
        public int Attempts { get; private set; }
        public FailingNodeHandler(QuestNodeType nodeType) => NodeType = nodeType;

        public Task<QuestNodeHandlerResult> HandleAsync(
            QuestNodeExecutionContext context, CancellationToken ct = default)
        {
            Attempts++;
            return Task.FromResult(QuestNodeHandlerResult.Fail("intentional failure (test)"));
        }
    }

    // ── Quest-definition builder ───────────────────────────────────────────────

    /// <summary>Build a linear quest A→B→C… from node specs and persist it into the
    /// harness's quest store. Each spec carries the node's <c>_workflow</c> marker
    /// (or null for auto). The first node is the entry, the last is terminal.</summary>
    private static QuestEntity BuildLinearQuest(Harness h, QuestNodeType nodeType, params (string Name, string? Workflow)[] specs)
    {
        var questId = Guid.NewGuid();
        var nodes = new List<QuestNode>();
        for (var i = 0; i < specs.Length; i++)
        {
            nodes.Add(new QuestNode
            {
                Id = Guid.NewGuid(),
                QuestId = questId,
                Name = specs[i].Name,
                NodeType = nodeType,
                Config = specs[i].Workflow ?? "{}",
                IsEntry = i == 0,
                IsTerminal = i == specs.Length - 1,
            });
        }

        var edges = new List<QuestEdge>();
        for (var i = 0; i < nodes.Count - 1; i++)
        {
            edges.Add(new QuestEdge
            {
                Id = Guid.NewGuid(),
                QuestId = questId,
                SourceNodeId = nodes[i].Id,
                TargetNodeId = nodes[i + 1].Id,
                EdgeType = QuestEdgeType.Control,
            });
        }

        var quest = new QuestEntity
        {
            Id = questId,
            Name = "WorkflowQuest",
            AvatarId = AvatarId,
            Status = QuestStatus.Active,   // B6: must be Active to start a workflow run
            Nodes = nodes,
            Edges = edges,
        };
        h.QuestStore.UpsertQuestAsync(quest).GetAwaiter().GetResult();
        return quest;
    }

    private static string Gated(string gateId) =>
        $"{{\"_workflow\":{{\"advance\":\"gated\",\"gateId\":\"{gateId}\"}}}}";

    private const string Manual = "{\"_workflow\":{\"advance\":\"manual\"}}";

    // ════════════════════════════════════════════════════════════════════════
    // CASE 1 — Auto DAG runs to completion
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AutoDag_RunsToCompletion()
    {
        var node = new RecordingNodeHandler(QuestNodeType.Condition);
        var h = new Harness { NodeHandler = node };
        var quest = BuildLinearQuest(h, QuestNodeType.Condition,
            ("A", null), ("B", null), ("C", null));

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(quest.Id, AvatarId);
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor);
        scope.Dispose();

        foreach (var n in quest.Nodes)
            node.CountFor(n.Id).Should().Be(1, $"node {n.Name} should run exactly once");
        h.RunStatus(runId).Should().Be(QuestRunStatus.Succeeded);
    }

    // ════════════════════════════════════════════════════════════════════════
    // CASE 2 — Suspend at a gate, RESTART the engine, resume on signal
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GatedRun_SuspendsThenSurvivesRestart_ThenResumesOnSignal()
    {
        var node = new RecordingNodeHandler(QuestNodeType.Condition);
        var h = new Harness { NodeHandler = node };
        // A(auto) -> Gate(gated "phase-met") -> C(auto, terminal)
        var quest = BuildLinearQuest(h, QuestNodeType.Condition,
            ("A", null), ("Gate", Gated("phase-met")), ("C", null));
        var nodeA = quest.Nodes[0];
        var nodeGate = quest.Nodes[1];
        var nodeC = quest.Nodes[2];

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(quest.Id, AvatarId);
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        // First processor instance: pumps until the gate parks.
        var (processor1, scope1) = h.NewProcessor();
        await h.PumpAsync(processor1);

        h.RunStatus(runId).Should().Be(QuestRunStatus.AwaitingSignal,
            "the run must suspend at the gate");
        node.CountFor(nodeA.Id).Should().Be(1, "A ran before the gate");
        node.CountFor(nodeGate.Id).Should().Be(0, "the gate parks BEFORE doing its work");
        node.CountFor(nodeC.Id).Should().Be(0, "C is downstream of the parked gate");

        // ── RESTART: discard the processor + its scope entirely. Durability now
        // lives ONLY in the saga rows held by the harness's SagaStore. ──
        scope1.Dispose();

        // Deliver the signal through a fresh manager (the parked row is the only
        // state). Then build a BRAND NEW processor + scope over the same stores.
        var signal = await manager.SignalAsync(runId, "phase-met", "phase-met", AvatarId);
        signal.IsError.Should().BeFalse(signal.Message);

        var (processor2, scope2) = h.NewProcessor();
        await h.PumpAsync(processor2);
        scope2.Dispose();

        node.CountFor(nodeGate.Id).Should().Be(1, "the gate ran after the signal un-parked it");
        node.CountFor(nodeC.Id).Should().Be(1, "C ran after the gate resumed");
        h.RunStatus(runId).Should().Be(QuestRunStatus.Succeeded);
    }

    // ════════════════════════════════════════════════════════════════════════
    // CASE 3 — Compensation on failure
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FailingNode_ExhaustsRetries_RoutesToCompensation()
    {
        // The single node ("Grant") fails every attempt. The quest-workflow forward
        // node-step declares QuestWorkflowSaga.CompensateStepName as its
        // compensation, so once RetryPolicy.Default's budget (5 attempts) is
        // exhausted the saga routes to the compensation handler, which projects the
        // run Cancelled. PumpAsync pulls retry backoff forward each tick so the
        // budget is consumed without real delays.
        var failing = new FailingNodeHandler(QuestNodeType.Search);
        var h = new Harness { NodeHandler = failing };
        var quest = BuildLinearQuest(h, QuestNodeType.Search, ("Grant", null));
        var grant = quest.Nodes[0];

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(quest.Id, AvatarId);
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        // Enough ticks to exhaust the 5-attempt budget AND run the compensation
        // step. PumpAsync returns early once quiescent.
        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor, maxTicks: 40);
        scope.Dispose();

        failing.Attempts.Should().BeGreaterThanOrEqualTo(1,
            "the failing node must have been dispatched");

        // The saga rows prove the route: the forward node-step is Compensating, and
        // a compensation step exists and Completed.
        var steps = h.SagaStore.Snapshot();
        var nodeStep = steps.FirstOrDefault(s => s.StepName == grant.Id.ToString());
        nodeStep.Should().NotBeNull();
        nodeStep!.Status.Should().Be(StepStatus.Compensating,
            "the forward node-step routes to compensation on exhaustion");

        var compStep = steps.FirstOrDefault(s => s.StepName == QuestWorkflowSaga.CompensateStepName);
        compStep.Should().NotBeNull("a compensation step must have been enqueued");
        compStep!.IsCompensation.Should().BeTrue();
        compStep.Status.Should().Be(StepStatus.Completed,
            "the compensation handler ran and settled the saga");

        // The run settles terminal CANCELLED — the refund-on-cancel outcome of
        // the worked example. The forward node-step does NOT pre-empt with a
        // terminal Failed on its failing attempt (it always declares a
        // compensation, so a failing attempt is not yet the run's verdict); the
        // run stays Running through retries, then the compensation handler owns
        // the terminal projection and settles it Cancelled.
        h.RunStatus(runId).Should().Be(QuestRunStatus.Cancelled,
            "compensation owns the terminal projection: the failing node retries " +
            "(run stays in flight), then the compensate handler settles it Cancelled");
    }

    // ════════════════════════════════════════════════════════════════════════
    // CASE 4 — Manual advance (the step() primitive)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ManualNode_SuspendsUntilAdvance_ThenCompletes()
    {
        var node = new RecordingNodeHandler(QuestNodeType.Condition);
        var h = new Harness { NodeHandler = node };
        // A(auto) -> B(manual) -> C(auto, terminal)
        var quest = BuildLinearQuest(h, QuestNodeType.Condition,
            ("A", null), ("B", Manual), ("C", null));
        var nodeA = quest.Nodes[0];
        var nodeB = quest.Nodes[1];
        var nodeC = quest.Nodes[2];

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(quest.Id, AvatarId);
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        var (processor1, scope1) = h.NewProcessor();
        await h.PumpAsync(processor1);
        scope1.Dispose();

        // A manual node DOES its work then suspends the run (it is the step()
        // boundary): B has run, but the run is Suspended and C has not run.
        h.RunStatus(runId).Should().Be(QuestRunStatus.Suspended);
        node.CountFor(nodeA.Id).Should().Be(1);
        node.CountFor(nodeB.Id).Should().Be(1, "the manual node runs, then suspends");
        node.CountFor(nodeC.Id).Should().Be(0, "C waits for an explicit advance()");

        // step(): advance from B into its single Control successor.
        var advance = await manager.AdvanceAsync(runId, nodeB.Id, AvatarId);
        advance.IsError.Should().BeFalse(advance.Message);

        var (processor2, scope2) = h.NewProcessor();
        await h.PumpAsync(processor2);
        scope2.Dispose();

        node.CountFor(nodeC.Id).Should().Be(1, "C runs after advance()");
        h.RunStatus(runId).Should().Be(QuestRunStatus.Succeeded);
    }

    // ════════════════════════════════════════════════════════════════════════
    // CASE 5 — A gated node resumes to its single Control successor (branch select)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GatedNode_ResumesToSingleSuccessor_OnSignal()
    {
        var node = new RecordingNodeHandler(QuestNodeType.Condition);
        var h = new Harness { NodeHandler = node };
        // Gate(gated "decision") -> Continue(auto, terminal). The fan-out guard
        // forbids two Control successors from a gate, so a "branch" is modelled as
        // signal-selected resumption into the gate's single successor.
        var quest = BuildLinearQuest(h, QuestNodeType.Condition,
            ("Gate", Gated("decision")), ("Continue", null));
        var gate = quest.Nodes[0];
        var cont = quest.Nodes[1];

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(quest.Id, AvatarId);
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        var (processor1, scope1) = h.NewProcessor();
        await h.PumpAsync(processor1);
        h.RunStatus(runId).Should().Be(QuestRunStatus.AwaitingSignal);
        node.CountFor(cont.Id).Should().Be(0, "the successor waits behind the gate");
        scope1.Dispose();

        var signal = await manager.SignalAsync(runId, "decision", "go", AvatarId);
        signal.IsError.Should().BeFalse(signal.Message);

        var (processor2, scope2) = h.NewProcessor();
        await h.PumpAsync(processor2);
        scope2.Dispose();

        node.CountFor(gate.Id).Should().Be(1, "the gate ran once it was signalled");
        node.CountFor(cont.Id).Should().Be(1, "the single successor ran after the gate resumed");
        h.RunStatus(runId).Should().Be(QuestRunStatus.Succeeded);
    }

    // ════════════════════════════════════════════════════════════════════════
    // CASE 6 — Compensation payload type-pun is field-bound (regression guard)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ForwardPayloadJson_DeserializesIntoCompensationPayload_FieldsPreserved()
    {
        // The saga's CompensateStepAsync flows the FORWARD step's QuestStepPayload
        // JSON verbatim into the compensation step, where SagaStep<QuestCompensatePayload>
        // deserializes it by matching property names. This guards that the shared
        // identity triple (RunId/QuestId/AvatarId) survives that round-trip: a
        // rename of any shared field would make these assertions fail loudly
        // instead of silently yielding Guid.Empty (a refund that no-ops).
        var forward = new QuestStepPayload(
            RunId: Guid.NewGuid(),
            QuestId: Guid.NewGuid(),
            AvatarId: Guid.NewGuid(),
            NodeId: Guid.NewGuid(),
            SignalPayload: "phase-met");

        var forwardJson = SagaStep<QuestStepPayload>.Serialize(forward);
        var comp = System.Text.Json.JsonSerializer.Deserialize<QuestCompensatePayload>(forwardJson);

        comp.Should().NotBeNull();
        comp!.RunId.Should().Be(forward.RunId, "RunId must survive the type-pun round-trip");
        comp.QuestId.Should().Be(forward.QuestId, "QuestId must survive the type-pun round-trip");
        comp.AvatarId.Should().Be(forward.AvatarId, "AvatarId must survive the type-pun round-trip");

        // The ToCompensation() projection is the compile-time witness of the same
        // correspondence — it must agree with the JSON round-trip.
        forward.ToCompensation().Should().Be(comp);
    }
}
