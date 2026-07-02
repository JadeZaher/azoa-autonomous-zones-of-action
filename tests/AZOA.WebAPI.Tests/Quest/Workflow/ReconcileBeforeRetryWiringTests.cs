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
/// End-to-end proof of the reconcile-before-retry WIRING (P7 / contract §7),
/// driving the REAL <see cref="SagaProcessor"/> + <see cref="QuestNodeStepHandler"/>
/// + <see cref="QuestManager"/> over the in-memory stores — the layer above the
/// pure <c>ChainActionRecovery</c> table (covered by ChainActionRecoveryTests).
///
/// <para>The headline SAFETY property: a Tier-2 chain-action node that broadcast a
/// tx then FAILED (e.g. a confirmation-read timeout) must NEVER re-broadcast. These
/// tests pin all three branches as they flow through the durable engine:</para>
/// <list type="bullet">
/// <item><b>Confirmed → no re-mint:</b> the node ran exactly once; the run advances
/// reconciled to Succeeded without a second dispatch.</item>
/// <item><b>FailedOnChain → retry/compensation:</b> the failure is handed back to
/// the saga budget (re-broadcast is provably safe).</item>
/// <item><b>Indeterminate (Unknown/Pending) → park:</b> the run parks in
/// <c>AwaitingReconciliation</c> and the node is NOT re-dispatched; a later
/// reconcile sweep with a now-Confirmed verdict resolves it WITHOUT re-minting.</item>
/// </list>
/// </summary>
public sealed class ReconcileBeforeRetryWiringTests
{
    private static readonly Guid AvatarId = Guid.NewGuid();

    // ── A Tier-2 chain-action node double ────────────────────────────────────
    // RequiresChainCapability => true so it routes through the reconcile-before-retry
    // branch on failure. It records every dispatch (to prove no double-mint) and
    // returns a configurable outcome: a broadcast-then-failure stamps a tx hash on
    // an IsError result (the double-mint scenario), a clean success stamps a hash
    // on an Ok result, a clean failure stamps nothing.
    private sealed class ChainActionNodeDouble : IQuestNodeHandler
    {
        private readonly bool _fail;
        private readonly string? _txHash;
        private readonly List<Guid> _dispatched = new();

        public ChainActionNodeDouble(QuestNodeType nodeType, bool fail, string? txHash)
        {
            NodeType = nodeType;
            _fail = fail;
            _txHash = txHash;
        }

        public QuestNodeType NodeType { get; }
        public bool RequiresChainCapability => true;

        public int DispatchCount { get { lock (_dispatched) return _dispatched.Count; } }

        public Task<QuestNodeHandlerResult> HandleAsync(
            QuestNodeExecutionContext context, CancellationToken ct = default)
        {
            lock (_dispatched) _dispatched.Add(context.NodeId);
            var result = _fail
                ? QuestNodeResults.Fail("broadcast then confirmation timeout (test)", txHash: _txHash, chainType: "Algorand")
                : QuestNodeResults.Ok("ok", txHash: _txHash, chainType: "Algorand");
            return Task.FromResult(result);
        }
    }

    // ── Harness (mirrors DurableWorkflowEngineTests, one chain-action node) ───
    private sealed class Harness
    {
        public InMemorySagaStore SagaStore { get; } = new();
        public InMemoryQuestStore QuestStore { get; } = new();
        public InMemoryQuestRunStore RunStore { get; } = new();
        public InMemoryQuestNodeExecutionStore ExecutionStore { get; } = new();
        public required ChainActionNodeDouble NodeHandler { get; init; }

        // A bound wallet so the Tier-2 chain-capability gate PASSES (else the node
        // is refused pre-execution and never reaches the reconcile branch).
        public IWalletManager WalletManager { get; } = WalletManagerMocks.WithOneWallet();

        // The single provider factory both the step handler (live park) and the
        // manager (sweep re-probe) resolve chain truth from. Mutable so a sweep test
        // can flip Unknown→Confirmed between phases.
        public IBlockchainProviderFactory ProviderFactory { get; set; } =
            BlockchainProviderFactoryFakes.Returning(ChainConfirmation.Unknown);

        public QuestManager NewManager() => new(
            QuestStore, RunStore, ExecutionStore, new QuestDagValidator(),
            new QuestNodeHandlerRegistry(new IQuestNodeHandler[] { NodeHandler }),
            SagaStore, WalletManager, ProviderFactory,
            BindingResolverFakes.PassThrough());

        public (SagaProcessor Processor, ServiceProvider Scope) NewProcessor()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ISagaStore>(SagaStore);
            services.AddSingleton<IQuestStore>(QuestStore);
            services.AddSingleton<IQuestRunStore>(RunStore);
            services.AddSingleton<IQuestNodeExecutionStore>(ExecutionStore);
            services.AddSingleton<IQuestNodeHandlerRegistry>(
                new QuestNodeHandlerRegistry(new IQuestNodeHandler[] { NodeHandler }));
            services.AddSingleton<IWalletManager>(WalletManager);
            services.AddSingleton(ProviderFactory);
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

        public async Task PumpAsync(SagaProcessor processor, int maxTicks = 40)
        {
            for (var tick = 0; tick < maxTicks; tick++)
            {
                SagaStore.PullForwardPendingRetries();
                var processed = await processor.ProcessDueStepsAsync(CancellationToken.None);
                if (processed == 0) return;
            }
        }

        public QuestRunStatus RunStatus(Guid runId) =>
            RunStore.GetByIdAsync(runId).GetAwaiter().GetResult().Result!.Status;

        public QuestNodeExecution Execution(Guid runId, Guid nodeId) =>
            ExecutionStore.GetByRunAndNodeAsync(runId, nodeId).GetAwaiter().GetResult().Result!;
    }

    /// <summary>A single-node quest whose only node is the chain-action node
    /// (entry + terminal). Keeps the DAG minimal so the assertion is purely about
    /// the reconcile branch, not graph topology.</summary>
    private static QuestEntity BuildSingleChainNodeQuest(Harness h)
    {
        var questId = Guid.NewGuid();
        var node = new QuestNode
        {
            Id = Guid.NewGuid(),
            QuestId = questId,
            Name = "Grant",
            NodeType = h.NodeHandler.NodeType,
            Config = "{}",
            IsEntry = true,
            IsTerminal = true,
        };
        var quest = new QuestEntity
        {
            Id = questId,
            Name = "ReconcileQuest",
            AvatarId = AvatarId,
            Status = QuestStatus.Active,
            Nodes = new List<QuestNode> { node },
            Edges = new List<QuestEdge>(),
        };
        h.QuestStore.UpsertQuestAsync(quest).GetAwaiter().GetResult();
        return quest;
    }

    // ════════════════════════════════════════════════════════════════════════
    // CONFIRMED → reconcile to success, NO re-mint
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ChainNode_FailedButTxConfirmed_AdvancesReconciled_DoesNotReMint()
    {
        // The headline double-mint scenario: the node "failed" (confirmation read
        // timed out) but stamped a tx hash that the chain reports CONFIRMED.
        var node = new ChainActionNodeDouble(QuestNodeType.Grant, fail: true, txHash: "TX_LANDED");
        var h = new Harness
        {
            NodeHandler = node,
            ProviderFactory = BlockchainProviderFactoryFakes.Returning(ChainConfirmation.Confirmed),
        };
        var quest = BuildSingleChainNodeQuest(h);
        var grant = quest.Nodes[0];

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(quest.Id, AvatarId);
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor);
        scope.Dispose();

        node.DispatchCount.Should().Be(1,
            "a confirmed tx must NEVER be re-broadcast — the node runs exactly once");
        var exec = h.Execution(runId, grant.Id);
        exec.State.Should().Be(QuestNodeState.Succeeded,
            "the failed-but-confirmed node reconciles to success");
        exec.TxHash.Should().Be("TX_LANDED", "the landed tx hash is recorded on the reconciled row");
        h.RunStatus(runId).Should().Be(QuestRunStatus.Succeeded);
    }

    // ════════════════════════════════════════════════════════════════════════
    // FAILED-ON-CHAIN → retry/compensation (safe to re-broadcast)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ChainNode_TxFailedOnChain_RoutesToRetryThenCompensation()
    {
        // Provably failed on-chain ⇒ re-broadcast is safe. The node-step fails into
        // the saga's retry/compensation budget; the run never parks for reconciliation.
        var node = new ChainActionNodeDouble(QuestNodeType.Grant, fail: true, txHash: "TX_REVERTED");
        var h = new Harness
        {
            NodeHandler = node,
            ProviderFactory = BlockchainProviderFactoryFakes.Returning(ChainConfirmation.FailedOnChain),
        };
        var quest = BuildSingleChainNodeQuest(h);

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(quest.Id, AvatarId);
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor, maxTicks: 60);
        scope.Dispose();

        h.RunStatus(runId).Should().NotBe(QuestRunStatus.AwaitingReconciliation,
            "a provably-failed tx is safe to retry — it must NOT park for reconciliation");
        // The saga budget owns the terminal verdict (Cancelled via compensation, or
        // Running if compensation dead-letters) — the key invariant is "not parked".
        h.RunStatus(runId).Should().BeOneOf(QuestRunStatus.Cancelled, QuestRunStatus.Running, QuestRunStatus.Failed);
    }

    // ════════════════════════════════════════════════════════════════════════
    // INDETERMINATE → park in AwaitingReconciliation, NO re-mint
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ChainNode_TxIndeterminate_ParksForReconciliation_DoesNotReMint()
    {
        // Broadcast then ambiguous chain result (Unknown). Re-broadcasting could
        // double-spend ⇒ park. The node must NOT be re-dispatched while parked.
        var node = new ChainActionNodeDouble(QuestNodeType.Grant, fail: true, txHash: "TX_IN_LIMBO");
        var h = new Harness
        {
            NodeHandler = node,
            ProviderFactory = BlockchainProviderFactoryFakes.Returning(ChainConfirmation.Unknown),
        };
        var quest = BuildSingleChainNodeQuest(h);
        var grant = quest.Nodes[0];

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(quest.Id, AvatarId);
        var runId = start.Result!.Id;

        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor);
        scope.Dispose();

        node.DispatchCount.Should().Be(1,
            "a parked node must NOT be re-dispatched — re-broadcast is the double-spend this prevents");
        h.RunStatus(runId).Should().Be(QuestRunStatus.AwaitingReconciliation,
            "an indeterminate verdict parks the run, never retries");
        var exec = h.Execution(runId, grant.Id);
        exec.State.Should().Be(QuestNodeState.Failed);
        exec.TxHash.Should().Be("TX_IN_LIMBO",
            "the hash is stamped on the parked row so a later sweep can re-probe it");
    }

    // ════════════════════════════════════════════════════════════════════════
    // SWEEP: a parked run resolves WITHOUT re-minting once the tx confirms
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReconcileSweep_OnParkedRun_ConfirmsTx_ResumesWithoutReMint()
    {
        // Phase 1: the node broadcasts then parks (Unknown). Phase 2: chain truth
        // becomes Confirmed; the sweep reconciles the parked run to success and the
        // engine completes it — WITHOUT ever re-dispatching the broadcasting node.
        var node = new ChainActionNodeDouble(QuestNodeType.Grant, fail: true, txHash: "TX_SETTLES_LATE");
        var h = new Harness
        {
            NodeHandler = node,
            ProviderFactory = BlockchainProviderFactoryFakes.Returning(ChainConfirmation.Unknown),
        };
        var quest = BuildSingleChainNodeQuest(h);
        var grant = quest.Nodes[0];

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(quest.Id, AvatarId);
        var runId = start.Result!.Id;

        // Phase 1 — park.
        var (p1, s1) = h.NewProcessor();
        await h.PumpAsync(p1);
        s1.Dispose();
        h.RunStatus(runId).Should().Be(QuestRunStatus.AwaitingReconciliation);
        node.DispatchCount.Should().Be(1);

        // Phase 2 — chain now reports CONFIRMED. Flip the harness factory so BOTH the
        // manager re-probe AND a freshly-built processor see the new verdict.
        h.ProviderFactory = BlockchainProviderFactoryFakes.Returning(ChainConfirmation.Confirmed);
        var sweepManager = h.NewManager();

        var sweep = await sweepManager.SweepReconciliationAsync();
        sweep.IsError.Should().BeFalse(sweep.Message);
        sweep.Result!.Should().ContainSingle()
            .Which.ReconciledConfirmed.Should().Be(1, "the parked node's tx confirmed");

        // The sweep un-parked the step; pump a fresh processor to let the engine
        // re-drive advancement from the now-Succeeded row.
        var (p2, s2) = h.NewProcessor();
        await h.PumpAsync(p2);
        s2.Dispose();

        node.DispatchCount.Should().Be(1,
            "reconciliation must resume from chain truth, NEVER re-broadcast the node");
        h.Execution(runId, grant.Id).State.Should().Be(QuestNodeState.Succeeded);
        h.RunStatus(runId).Should().Be(QuestRunStatus.Succeeded);
    }

    [Fact]
    public async Task ReconcileRun_RejectsRunNotAwaitingReconciliation()
    {
        // The manual re-probe is only valid for a parked run — a Running/fresh run
        // is rejected (state-machine guard), never silently reconciled.
        var node = new ChainActionNodeDouble(QuestNodeType.Grant, fail: false, txHash: "TX");
        var h = new Harness { NodeHandler = node };
        var quest = BuildSingleChainNodeQuest(h);

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(quest.Id, AvatarId);
        var runId = start.Result!.Id;

        var reconcile = await manager.ReconcileRunAsync(runId, AvatarId);
        reconcile.IsError.Should().BeTrue(
            "only AwaitingReconciliation runs accept a manual re-probe");
        reconcile.Message.Should().Contain("AwaitingReconciliation");
    }
}
