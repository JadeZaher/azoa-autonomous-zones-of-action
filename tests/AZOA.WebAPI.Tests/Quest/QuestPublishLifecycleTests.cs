using FluentAssertions;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Tests.Quest;

/// <summary>
/// Pins the publish-gate / definition-lifecycle semantics (FR-2 + FR-3 / AC-2a–2e + AC-3a/3b).
/// See Managers/AGENTS.md §publish-lifecycle.
/// </summary>
public class QuestPublishLifecycleTests
{
    private static readonly Guid AvatarId = Guid.NewGuid();

    // ─── helpers ───────────────────────────────────────────────────────────────

    private static (QuestManager manager, InMemoryQuestStore questStore, InMemoryQuestRunStore runStore)
        BuildManager(QuestEntity? quest = null)
    {
        var questStore = new InMemoryQuestStore();
        var runStore   = new InMemoryQuestRunStore();
        if (quest != null)
            questStore.UpsertQuestAsync(quest).GetAwaiter().GetResult();

        var manager = new QuestManager(
            questStore,
            runStore,
            new InMemoryQuestNodeExecutionStore(),
            new QuestDagValidator(),
            new QuestNodeHandlerRegistry(Array.Empty<IQuestNodeHandler>()),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning());

        return (manager, questStore, runStore);
    }

    /// <summary>Minimal valid linear 2-node quest in Draft state.</summary>
    private static QuestEntity ValidDraftQuest(Guid? avatarId = null)
    {
        var entryId    = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var qid = Guid.NewGuid();
        return new QuestEntity
        {
            Id       = qid,
            Name     = "Valid draft",
            AvatarId = avatarId ?? AvatarId,
            Status   = QuestStatus.Draft,
            Nodes = new List<QuestNode>
            {
                new() { Id = entryId,    Name = "Entry",    NodeType = QuestNodeType.Condition,
                        IsEntry = true, IsTerminal = false, Config = "{}" },
                new() { Id = terminalId, Name = "Terminal", NodeType = QuestNodeType.Condition,
                        IsEntry = false, IsTerminal = true, Config = "{}" },
            },
            Edges = new List<QuestEdge>
            {
                new() { Id = Guid.NewGuid(), QuestId = qid,
                        SourceNodeId = entryId, TargetNodeId = terminalId,
                        EdgeType = QuestEdgeType.Control },
            }
        };
    }

    // ─── AC-2a: publish valid quest → Active ──────────────────────────────────

    [Fact]
    public async Task Publish_ValidQuest_FlipsToDraftToActive()
    {
        var quest = ValidDraftQuest();
        var (manager, _, _) = BuildManager(quest);

        var result = await manager.PublishAsync(quest.Id, quest.AvatarId);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(QuestStatus.Active);
    }

    // ─── AC-2a: publishing a cyclic quest fails, quest stays Draft ────────────

    [Fact]
    public async Task Publish_CyclicQuest_Fails_StaysDraft()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var qid = Guid.NewGuid();

        var quest = new QuestEntity
        {
            Id       = qid,
            Name     = "Cyclic",
            AvatarId = AvatarId,
            Status   = QuestStatus.Draft,
            Nodes = new List<QuestNode>
            {
                new() { Id = aId, Name = "A", IsEntry = true,  IsTerminal = false, NodeType = QuestNodeType.Condition, Config = "{}" },
                new() { Id = bId, Name = "B", IsEntry = false, IsTerminal = false, NodeType = QuestNodeType.Condition, Config = "{}" },
            },
            Edges = new List<QuestEdge>
            {
                new() { Id = Guid.NewGuid(), QuestId = qid, SourceNodeId = aId, TargetNodeId = bId, EdgeType = QuestEdgeType.Control },
                new() { Id = Guid.NewGuid(), QuestId = qid, SourceNodeId = bId, TargetNodeId = aId, EdgeType = QuestEdgeType.Control },
            }
        };
        var (manager, questStore, _) = BuildManager(quest);

        var result = await manager.PublishAsync(quest.Id, quest.AvatarId);

        result.IsError.Should().BeTrue("cyclic DAG must be rejected");
        result.Message.Should().ContainEquivalentOf("Cycle");

        // Quest must remain Draft after a failed publish.
        var reloaded = (await questStore.GetQuestAsync(quest.Id)).Result;
        reloaded!.Status.Should().Be(QuestStatus.Draft);
    }

    // ─── AC-2b: ExecuteAsync on Draft quest returns error naming publish ───────

    [Fact]
    public async Task ExecuteAsync_DraftQuest_ReturnsPublishRequiredError()
    {
        var quest = ValidDraftQuest();
        var (manager, _, _) = BuildManager(quest);

        var result = await manager.ExecuteAsync(quest.Id, quest.AvatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().ContainEquivalentOf("publish");
    }

    [Fact]
    public async Task StartWorkflowRunAsync_DraftQuest_ReturnsPublishRequiredError()
    {
        var quest = ValidDraftQuest();
        var (manager, _, _) = BuildManager(quest);

        var result = await manager.StartWorkflowRunAsync(quest.Id, quest.AvatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().ContainEquivalentOf("publish");
    }

    // ─── AC-2c: mutations on Active quest rejected; succeed after unpublish ───

    [Fact]
    public async Task AddNodeAsync_ActiveQuest_Rejected()
    {
        var quest = ValidDraftQuest();
        quest.Status = QuestStatus.Active;
        var (manager, _, _) = BuildManager(quest);

        var result = await manager.AddNodeAsync(quest.Id,
            new QuestNodeCreateModel { Name = "X", NodeType = QuestNodeType.Condition, Config = "{}" },
            quest.AvatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().ContainEquivalentOf("unpublish");
    }

    [Fact]
    public async Task AddNodeAsync_AfterUnpublish_Succeeds()
    {
        var quest = ValidDraftQuest();
        var (manager, _, _) = BuildManager(quest);

        // Publish first, then unpublish.
        await manager.PublishAsync(quest.Id, quest.AvatarId);
        await manager.UnpublishAsync(quest.Id, quest.AvatarId);

        var result = await manager.AddNodeAsync(quest.Id,
            new QuestNodeCreateModel { Name = "X", NodeType = QuestNodeType.Condition, Config = "{}" },
            quest.AvatarId);

        result.IsError.Should().BeFalse("mutations succeed after unpublish");
    }

    // ─── AC-2d: unpublish refused while in-flight run exists ──────────────────

    [Fact]
    public async Task UnpublishAsync_WithInFlightRun_Refused()
    {
        var quest = ValidDraftQuest();
        var (manager, _, runStore) = BuildManager(quest);

        // Publish the quest.
        await manager.PublishAsync(quest.Id, quest.AvatarId);

        // Seed a non-terminal run directly into the store.
        await runStore.CreateAsync(new QuestRun
        {
            Id       = Guid.NewGuid(),
            QuestId  = quest.Id,
            AvatarId = quest.AvatarId,
            Status   = QuestRunStatus.Running,
        });

        var result = await manager.UnpublishAsync(quest.Id, quest.AvatarId);

        result.IsError.Should().BeTrue("in-flight run blocks unpublish");
        result.Message.Should().ContainEquivalentOf("in-flight");
    }

    [Fact]
    public async Task UnpublishAsync_AllRunsTerminal_Succeeds()
    {
        var quest = ValidDraftQuest();
        var (manager, _, runStore) = BuildManager(quest);

        await manager.PublishAsync(quest.Id, quest.AvatarId);

        // Seed a terminal run.
        await runStore.CreateAsync(new QuestRun
        {
            Id       = Guid.NewGuid(),
            QuestId  = quest.Id,
            AvatarId = quest.AvatarId,
            Status   = QuestRunStatus.Succeeded,
        });

        var result = await manager.UnpublishAsync(quest.Id, quest.AvatarId);

        result.IsError.Should().BeFalse("all runs terminal — unpublish allowed");
        result.Result!.Status.Should().Be(QuestStatus.Draft);
    }

    // ─── AC-3a: fan-out quest fails publish and StartWorkflowRunAsync ─────────

    private static QuestEntity FanOutQuest()
    {
        var entryId = Guid.NewGuid();
        var b1Id    = Guid.NewGuid();
        var b2Id    = Guid.NewGuid();
        var qid     = Guid.NewGuid();
        return new QuestEntity
        {
            Id       = qid,
            Name     = "FanOut",
            AvatarId = AvatarId,
            Status   = QuestStatus.Draft,
            Nodes = new List<QuestNode>
            {
                new() { Id = entryId, Name = "Entry", IsEntry = true,  IsTerminal = false, NodeType = QuestNodeType.Condition, Config = "{}" },
                new() { Id = b1Id,    Name = "B1",    IsEntry = false, IsTerminal = true,  NodeType = QuestNodeType.Condition, Config = "{}" },
                new() { Id = b2Id,    Name = "B2",    IsEntry = false, IsTerminal = true,  NodeType = QuestNodeType.Condition, Config = "{}" },
            },
            Edges = new List<QuestEdge>
            {
                new() { Id = Guid.NewGuid(), QuestId = qid, SourceNodeId = entryId, TargetNodeId = b1Id, EdgeType = QuestEdgeType.Control },
                new() { Id = Guid.NewGuid(), QuestId = qid, SourceNodeId = entryId, TargetNodeId = b2Id, EdgeType = QuestEdgeType.Control },
            }
        };
    }

    [Fact]
    public async Task Publish_FanOutQuest_Fails()
    {
        var quest = FanOutQuest();
        var (manager, _, _) = BuildManager(quest);

        var result = await manager.PublishAsync(quest.Id, quest.AvatarId);

        result.IsError.Should().BeTrue("fan-out is rejected at publish time (AC-3a)");
        result.Message.Should().ContainEquivalentOf("fan-out");
    }

    // ─── AC-3b: same fan-out quest still executes on legacy path (warning) ────

    [Fact]
    public void DagValidator_FanOut_IsWarningOnLegacyPath()
    {
        var quest = FanOutQuest();
        var validator = new QuestDagValidator();

        // Default call (fanOutAsError: false) → warning, not error.
        var result = validator.Validate(quest, fanOutAsError: false);

        result.Warnings.Should().Contain(w => w.Contains("fan-out", StringComparison.OrdinalIgnoreCase),
            "fan-out is a warning on the legacy executor path (AC-3b)");
        result.IsValid.Should().BeTrue("fan-out warning does not fail the structural check");
    }

    [Fact]
    public void DagValidator_FanOut_IsErrorOnDurablePath()
    {
        var quest = FanOutQuest();
        var validator = new QuestDagValidator();

        var result = validator.Validate(quest, fanOutAsError: true);

        result.IsValid.Should().BeFalse("fan-out is an error on the durable/publish path (AC-3a)");
        result.Errors.Should().Contain(e => e.Contains("fan-out", StringComparison.OrdinalIgnoreCase));
        result.Warnings.Should().BeEmpty("error path puts it in Errors, not Warnings");
    }
}
