using FluentAssertions;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Tests.Quest;

/// <summary>
/// Pins the cascade-skip semantics introduced by quest-dag-semantic-hardening
/// (FR-1 / AC-1a–1d). See Managers/AGENTS.md §skip-semantics.
/// </summary>
public class QuestManagerSkipPropagationTests
{
    // ─── Scaffolding ───────────────────────────────────────────────────

    /// <summary>
    /// Builds a quest with a GateCheck →(Control) Transfer1 →(Control) Transfer2
    /// chain. GateCheck is wired to a failing handler so it produces Failed.
    /// Returns the manager + run after execution so callers can inspect node states.
    /// </summary>
    private static async Task<(QuestManager manager,
                                InMemoryQuestNodeExecutionStore execStore,
                                QuestEntity quest,
                                QuestRun run)>
        BuildAndExecuteGateChainAsync(bool gateCheckPasses)
    {
        var questStore = new InMemoryQuestStore();
        var runStore   = new InMemoryQuestRunStore();
        var execStore  = new InMemoryQuestNodeExecutionStore();

        var gateId  = Guid.NewGuid();
        var t1Id    = Guid.NewGuid();
        var t2Id    = Guid.NewGuid();
        var avatarId = Guid.NewGuid();

        var quest = new QuestEntity
        {
            Id       = Guid.NewGuid(),
            Name     = "Gate chain",
            AvatarId = avatarId,
            Status   = QuestStatus.Active,
            Nodes = new List<QuestNode>
            {
                new() { Id = gateId, Name = "Gate", NodeType = QuestNodeType.GateCheck,
                        Config = "{\"predicate\":\"true\",\"reads\":{},\"holons\":[]}",
                        IsEntry = true, IsTerminal = false },
                new() { Id = t1Id, Name = "Transfer1", NodeType = QuestNodeType.Transfer,
                        Config = "{\"nftId\":\"00000000000000000000000000000001\",\"request\":{}}",
                        IsEntry = false, IsTerminal = false },
                new() { Id = t2Id, Name = "Transfer2", NodeType = QuestNodeType.Transfer,
                        Config = "{\"nftId\":\"00000000000000000000000000000002\",\"request\":{}}",
                        IsEntry = false, IsTerminal = true },
            },
            Edges = new List<QuestEdge>
            {
                new() { Id = Guid.NewGuid(), QuestId = Guid.Empty, SourceNodeId = gateId,  TargetNodeId = t1Id, EdgeType = QuestEdgeType.Control },
                new() { Id = Guid.NewGuid(), QuestId = Guid.Empty, SourceNodeId = t1Id,   TargetNodeId = t2Id, EdgeType = QuestEdgeType.Control },
            }
        };

        // Assign topological order manually (validator not run here).
        quest.Nodes[0].ExecutionOrder = 0;
        quest.Nodes[1].ExecutionOrder = 1;
        quest.Nodes[2].ExecutionOrder = 2;

        await questStore.UpsertQuestAsync(quest);

        // A handler that honours gateCheckPasses for GateCheck; Transfer handlers
        // are irrelevant when skipped but must be present so the registry doesn't
        // short-circuit.
        var handlers = new IQuestNodeHandler[]
        {
            new ConfigurableGateCheckHandler(passes: gateCheckPasses),
            new AlwaysSucceedHandler(QuestNodeType.Transfer),
        };

        var manager = new QuestManager(
            questStore,
            runStore,
            execStore,
            new QuestDagValidator(),
            new QuestNodeHandlerRegistry(handlers),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var runResult = await manager.ExecuteAsync(quest.Id, avatarId);
        runResult.IsError.Should().BeFalse("execution should succeed even when gate fails");

        return (manager, execStore, quest, runResult.Result!);
    }

    // ─── AC-1a: GateCheck →(Control) Transfer1 →(Control) Transfer2 ───────────
    // Gate fails ⇒ BOTH Transfer1 AND Transfer2 must be Skipped.

    [Fact]
    public async Task Control_Chain_GateFails_BothSuccessorsSkipped()
    {
        var (_, execStore, quest, run) = await BuildAndExecuteGateChainAsync(gateCheckPasses: false);

        var executions = (await execStore.GetByRunIdAsync(run.Id)).Result!.ToList();

        var gateExec  = executions.First(e => e.NodeId == quest.Nodes[0].Id);
        var t1Exec    = executions.First(e => e.NodeId == quest.Nodes[1].Id);
        var t2Exec    = executions.First(e => e.NodeId == quest.Nodes[2].Id);

        gateExec.State.Should().Be(QuestNodeState.Failed,    "gate predicate failed");
        t1Exec.State.Should().Be(QuestNodeState.Skipped,     "Transfer1 behind failed gate");
        t2Exec.State.Should().Be(QuestNodeState.Skipped,     "Transfer2 behind skipped Transfer1 (cascade)");
    }

    [Fact]
    public async Task Control_Chain_GatePasses_BothSuccessorsSucceeded()
    {
        var (_, execStore, quest, run) = await BuildAndExecuteGateChainAsync(gateCheckPasses: true);

        var executions = (await execStore.GetByRunIdAsync(run.Id)).Result!.ToList();
        var t2Exec = executions.First(e => e.NodeId == quest.Nodes[2].Id);

        // Happy path: none skipped.
        t2Exec.State.Should().Be(QuestNodeState.Succeeded, "all nodes run when gate passes");
    }

    // ─── AC-1b: Conditional edge with EMPTY Condition skips on Failed/Skipped ──

    [Fact]
    public async Task Conditional_EmptyCondition_SourceFailed_TargetSkipped()
    {
        var questStore = new InMemoryQuestStore();
        var runStore   = new InMemoryQuestRunStore();
        var execStore  = new InMemoryQuestNodeExecutionStore();

        var srcId  = Guid.NewGuid();
        var tgtId  = Guid.NewGuid();
        var avatarId = Guid.NewGuid();

        var quest = new QuestEntity
        {
            Id       = Guid.NewGuid(),
            Name     = "Conditional empty",
            AvatarId = avatarId,
            Status   = QuestStatus.Active,
            Nodes = new List<QuestNode>
            {
                new() { Id = srcId, Name = "Src", NodeType = QuestNodeType.GateCheck,
                        Config = "{\"predicate\":\"true\",\"reads\":{},\"holons\":[]}",
                        IsEntry = true, IsTerminal = false },
                new() { Id = tgtId, Name = "Tgt", NodeType = QuestNodeType.HolonGet,
                        Config = "{\"id\":\"00000000000000000000000000000001\"}",
                        IsEntry = false, IsTerminal = true },
            },
            Edges = new List<QuestEdge>
            {
                new() { Id = Guid.NewGuid(), QuestId = Guid.Empty,
                        SourceNodeId = srcId, TargetNodeId = tgtId,
                        EdgeType = QuestEdgeType.Conditional,
                        Condition = "" },   // intentionally empty
            }
        };
        quest.Nodes[0].ExecutionOrder = 0;
        quest.Nodes[1].ExecutionOrder = 1;
        await questStore.UpsertQuestAsync(quest);

        var manager = new QuestManager(
            questStore, runStore, execStore, new QuestDagValidator(),
            new QuestNodeHandlerRegistry(new IQuestNodeHandler[]
            {
                new ConfigurableGateCheckHandler(passes: false),
                new AlwaysSucceedHandler(QuestNodeType.HolonGet),
            }),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var runResult = await manager.ExecuteAsync(quest.Id, avatarId);
        runResult.IsError.Should().BeFalse();

        var executions = (await execStore.GetByRunIdAsync(runResult.Result!.Id)).Result!.ToList();
        executions.First(e => e.NodeId == tgtId).State.Should().Be(QuestNodeState.Skipped,
            "empty-condition Conditional edge propagates skip on source Failed");
    }

    [Fact]
    public async Task Conditional_EmptyCondition_SourceSkipped_TargetSkipped()
    {
        // Three-node chain: A(fails) →(Control) B →(Conditional, empty) C
        // B is Skipped (Control from Failed A); C must also be Skipped.
        var questStore = new InMemoryQuestStore();
        var runStore   = new InMemoryQuestRunStore();
        var execStore  = new InMemoryQuestNodeExecutionStore();

        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var cId = Guid.NewGuid();
        var avatarId = Guid.NewGuid();

        var quest = new QuestEntity
        {
            Id       = Guid.NewGuid(),
            Name     = "Cascade to conditional",
            AvatarId = avatarId,
            Status   = QuestStatus.Active,
            Nodes = new List<QuestNode>
            {
                new() { Id = aId, Name = "A", NodeType = QuestNodeType.GateCheck,
                        Config = "{\"predicate\":\"true\",\"reads\":{},\"holons\":[]}",
                        IsEntry = true, IsTerminal = false },
                new() { Id = bId, Name = "B", NodeType = QuestNodeType.HolonGet,
                        Config = "{\"id\":\"00000000000000000000000000000001\"}",
                        IsEntry = false, IsTerminal = false },
                new() { Id = cId, Name = "C", NodeType = QuestNodeType.HolonGet,
                        Config = "{\"id\":\"00000000000000000000000000000001\"}",
                        IsEntry = false, IsTerminal = true },
            },
            Edges = new List<QuestEdge>
            {
                new() { Id = Guid.NewGuid(), QuestId = Guid.Empty,
                        SourceNodeId = aId, TargetNodeId = bId,
                        EdgeType = QuestEdgeType.Control },
                new() { Id = Guid.NewGuid(), QuestId = Guid.Empty,
                        SourceNodeId = bId, TargetNodeId = cId,
                        EdgeType = QuestEdgeType.Conditional, Condition = "" },
            }
        };
        quest.Nodes[0].ExecutionOrder = 0;
        quest.Nodes[1].ExecutionOrder = 1;
        quest.Nodes[2].ExecutionOrder = 2;
        await questStore.UpsertQuestAsync(quest);

        var manager = new QuestManager(
            questStore, runStore, execStore, new QuestDagValidator(),
            new QuestNodeHandlerRegistry(new IQuestNodeHandler[]
            {
                new ConfigurableGateCheckHandler(passes: false),
                new AlwaysSucceedHandler(QuestNodeType.HolonGet),
            }),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var runResult = await manager.ExecuteAsync(quest.Id, avatarId);
        runResult.IsError.Should().BeFalse();

        var executions = (await execStore.GetByRunIdAsync(runResult.Result!.Id)).Result!.ToList();
        executions.First(e => e.NodeId == bId).State.Should().Be(QuestNodeState.Skipped);
        executions.First(e => e.NodeId == cId).State.Should().Be(QuestNodeState.Skipped,
            "Skipped source behind empty-condition Conditional edge cascades skip");
    }

    // ─── Happy path: all predecessors Completed ⇒ target runs ──────────────────

    [Fact]
    public async Task Control_Chain_AllSucceeded_TargetRuns()
    {
        var questStore = new InMemoryQuestStore();
        var runStore   = new InMemoryQuestRunStore();
        var execStore  = new InMemoryQuestNodeExecutionStore();

        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var avatarId = Guid.NewGuid();

        var quest = new QuestEntity
        {
            Id       = Guid.NewGuid(),
            Name     = "Happy chain",
            AvatarId = avatarId,
            Status   = QuestStatus.Active,
            Nodes = new List<QuestNode>
            {
                new() { Id = aId, Name = "A", NodeType = QuestNodeType.HolonGet,
                        Config = "{\"id\":\"00000000000000000000000000000001\"}",
                        IsEntry = true, IsTerminal = false },
                new() { Id = bId, Name = "B", NodeType = QuestNodeType.HolonGet,
                        Config = "{\"id\":\"00000000000000000000000000000001\"}",
                        IsEntry = false, IsTerminal = true },
            },
            Edges = new List<QuestEdge>
            {
                new() { Id = Guid.NewGuid(), QuestId = Guid.Empty,
                        SourceNodeId = aId, TargetNodeId = bId,
                        EdgeType = QuestEdgeType.Control },
            }
        };
        quest.Nodes[0].ExecutionOrder = 0;
        quest.Nodes[1].ExecutionOrder = 1;
        await questStore.UpsertQuestAsync(quest);

        var manager = new QuestManager(
            questStore, runStore, execStore, new QuestDagValidator(),
            new QuestNodeHandlerRegistry(new IQuestNodeHandler[]
            {
                new AlwaysSucceedHandler(QuestNodeType.HolonGet),
            }),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var runResult = await manager.ExecuteAsync(quest.Id, avatarId);
        runResult.IsError.Should().BeFalse();

        var executions = (await execStore.GetByRunIdAsync(runResult.Result!.Id)).Result!.ToList();
        executions.First(e => e.NodeId == bId).State.Should().Be(QuestNodeState.Succeeded,
            "no skip when predecessor succeeded");
    }

    // ─── Inline test-double handlers ────────────────────────────────────────────

    private sealed class ConfigurableGateCheckHandler : IQuestNodeHandler
    {
        private readonly bool _passes;
        public ConfigurableGateCheckHandler(bool passes) => _passes = passes;
        public QuestNodeType NodeType => QuestNodeType.GateCheck;
        public Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext ctx, CancellationToken ct = default)
            => Task.FromResult(_passes
                ? QuestNodeResults.Ok("{\"pass\":true}")
                : QuestNodeResults.Fail("gate not met"));
    }

    private sealed class AlwaysSucceedHandler : IQuestNodeHandler
    {
        public AlwaysSucceedHandler(QuestNodeType nodeType) => NodeType = nodeType;
        public QuestNodeType NodeType { get; }
        public Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext ctx, CancellationToken ct = default)
            => Task.FromResult(QuestNodeResults.Ok("{}"));
    }
}
