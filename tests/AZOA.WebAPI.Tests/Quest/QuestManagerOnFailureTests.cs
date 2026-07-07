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
/// Legacy-engine OnFailure semantics: V2 skip rule + V3 handled-failure run-status.
/// AC-2a: exactly-one-arm property (both directions).
/// AC-2c: handled failure does not mark the run Failed.
/// </summary>
public class QuestManagerOnFailureTests
{
    private static readonly Guid AvatarId = Guid.NewGuid();

    // ── Harness ──────────────────────────────────────────────────────────────

    private static (QuestManager Manager, InMemoryQuestNodeExecutionStore ExecStore)
        MakeHarness(InMemoryQuestStore questStore, params IQuestNodeHandler[] handlers)
    {
        var execStore = new InMemoryQuestNodeExecutionStore();
        var manager = new QuestManager(
            questStore,
            new InMemoryQuestRunStore(),
            execStore,
            new QuestDagValidator(), new QuestDagExecutabilityValidator(),
            new QuestNodeHandlerRegistry(handlers),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());
        return (manager, execStore);
    }

    // ── Quest factory: Entry →(Control) success | Entry →(OnFailure) failArm ─

    private static (InMemoryQuestStore Store, QuestEntity Quest,
                    Guid EntryId, Guid SuccessId, Guid FailArmId)
        BuildOnFailureQuest(QuestNodeType entryType)
    {
        var questId   = Guid.NewGuid();
        var entryId   = Guid.NewGuid();
        var successId = Guid.NewGuid();
        var failArmId = Guid.NewGuid();

        var quest = new QuestEntity
        {
            Id = questId, AvatarId = AvatarId,
            Name = "OnFailureTest", Status = QuestStatus.Active,
            Nodes =
            [
                new QuestNode
                {
                    Id = entryId, QuestId = questId, Name = "entry",
                    NodeType = entryType,
                    IsEntry = true, IsTerminal = false, ExecutionOrder = 0
                },
                new QuestNode
                {
                    Id = successId, QuestId = questId, Name = "success-arm",
                    NodeType = QuestNodeType.Transfer,
                    IsEntry = false, IsTerminal = true, ExecutionOrder = 1
                },
                new QuestNode
                {
                    Id = failArmId, QuestId = questId, Name = "failure-arm",
                    NodeType = QuestNodeType.Transfer,
                    IsEntry = false, IsTerminal = true, ExecutionOrder = 2
                },
            ],
            Edges =
            [
                new QuestEdge
                {
                    Id = Guid.NewGuid(), QuestId = questId,
                    SourceNodeId = entryId, TargetNodeId = successId,
                    EdgeType = QuestEdgeType.Control
                },
                new QuestEdge
                {
                    Id = Guid.NewGuid(), QuestId = questId,
                    SourceNodeId = entryId, TargetNodeId = failArmId,
                    EdgeType = QuestEdgeType.OnFailure
                },
            ]
        };

        var store = new InMemoryQuestStore();
        store.UpsertQuestAsync(quest).GetAwaiter().GetResult();
        return (store, quest, entryId, successId, failArmId);
    }

    // ── AC-2a / AC-2c: source Fails → failure arm runs, success arm skipped,
    //                   run Succeeded (handled failure, V3) ────────────────────

    [Fact]
    public async Task OnFailure_SourceFails_FailArmSucceeds_SuccessArmSkipped_RunSucceeded()
    {
        var (store, quest, entryId, successId, failArmId) =
            BuildOnFailureQuest(QuestNodeType.GateCheck);

        var (manager, execStore) = MakeHarness(store,
            new AlwaysFailHandler(QuestNodeType.GateCheck),
            new AlwaysSucceedHandler(QuestNodeType.Transfer));

        var result = await manager.ExecuteAsync(quest.Id, AvatarId);

        result.IsError.Should().BeFalse(because: result.Message ?? "no error");
        result.Result!.Status.Should().Be(QuestRunStatus.Succeeded,
            because: "Failed node has an OnFailure edge → handled (V3) → run Succeeded");

        var execs = (await execStore.GetByRunIdAsync(result.Result!.Id)).Result!.ToList();
        execs.Should().HaveCount(3);

        Exec(execs, entryId).State.Should().Be(QuestNodeState.Failed);
        Exec(execs, failArmId).State.Should().Be(QuestNodeState.Succeeded,
            because: "failure arm activated when source Failed");
        Exec(execs, successId).State.Should().Be(QuestNodeState.Skipped,
            because: "success arm skipped when source Failed");
    }

    // ── AC-2a: source Succeeds → success arm runs, failure arm skipped ────────

    [Fact]
    public async Task OnFailure_SourceSucceeds_SuccessArmRuns_FailArmSkipped()
    {
        var (store, quest, entryId, successId, failArmId) =
            BuildOnFailureQuest(QuestNodeType.Transfer);

        var (manager, execStore) = MakeHarness(store,
            new AlwaysSucceedHandler(QuestNodeType.Transfer));

        var result = await manager.ExecuteAsync(quest.Id, AvatarId);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(QuestRunStatus.Succeeded);

        var execs = (await execStore.GetByRunIdAsync(result.Result!.Id)).Result!.ToList();
        Exec(execs, entryId).State.Should().Be(QuestNodeState.Succeeded);
        Exec(execs, successId).State.Should().Be(QuestNodeState.Succeeded,
            because: "success arm runs when source Succeeded");
        Exec(execs, failArmId).State.Should().Be(QuestNodeState.Skipped,
            because: "failure arm skipped when source Succeeded (inverse activation)");
    }

    // ── AC-2c: unhandled failure (no OnFailure edge) marks run Failed ─────────

    [Fact]
    public async Task NoOnFailureEdge_SourceFails_RunFailed()
    {
        var questId = Guid.NewGuid();
        var nodeId  = Guid.NewGuid();

        var quest = new QuestEntity
        {
            Id = questId, AvatarId = AvatarId,
            Name = "UnhandledFail", Status = QuestStatus.Active,
            Nodes =
            [
                new QuestNode
                {
                    Id = nodeId, QuestId = questId, Name = "broken",
                    NodeType = QuestNodeType.Transfer,
                    IsEntry = true, IsTerminal = true, ExecutionOrder = 0
                },
            ],
            Edges = []
        };

        var store = new InMemoryQuestStore();
        await store.UpsertQuestAsync(quest);

        var (manager, _) = MakeHarness(store,
            new AlwaysFailHandler(QuestNodeType.Transfer));

        var result = await manager.ExecuteAsync(quest.Id, AvatarId);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(QuestRunStatus.Failed,
            because: "no OnFailure edge → failure is unhandled (V3)");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static QuestNodeExecution Exec(List<QuestNodeExecution> execs, Guid nodeId) =>
        execs.Single(e => e.NodeId == nodeId);

    private sealed class AlwaysSucceedHandler(QuestNodeType type) : IQuestNodeHandler
    {
        public QuestNodeType NodeType => type;
        public Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext ctx, CancellationToken ct = default)
            => Task.FromResult(QuestNodeHandlerResult.Ok(output: null));
    }

    private sealed class AlwaysFailHandler(QuestNodeType type) : IQuestNodeHandler
    {
        public QuestNodeType NodeType => type;
        public Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext ctx, CancellationToken ct = default)
            => Task.FromResult(QuestNodeHandlerResult.Fail("forced failure"));
    }
}
