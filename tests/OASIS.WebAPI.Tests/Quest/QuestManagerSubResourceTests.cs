using FluentAssertions;
using OASIS.WebAPI.Interfaces.QuestExecution;
using OASIS.WebAPI.Managers;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Providers.Stores;
using OASIS.WebAPI.Services;
using OASIS.WebAPI.Services.Quest;
using OASIS.WebAPI.Tests.Fakes;
using Xunit;
using QuestEntity = OASIS.WebAPI.Models.Quest.Quest;

namespace OASIS.WebAPI.Tests.Quest;

/// <summary>
/// Manager-level unit tests for the post-hoc sub-resource CRUD endpoints
/// (Nodes / Edges / Dependencies) plus the QuestRun read surface added by the
/// quest-api endpoint gap fill (RUNBOOK section 5 Phase F).
///
/// <para>
/// Uses real InMemory stores (rather than Mocks) so quest mutations actually
/// persist between manager calls — many of these methods are
/// load-then-mutate-then-save round trips and a Mocked GetQuestAsync would
/// return a stale snapshot on the second read.
/// </para>
/// </summary>
public class QuestManagerSubResourceTests
{
    // ─── Test scaffolding ───

    private static (QuestManager manager,
                    InMemoryQuestStore questStore,
                    InMemoryQuestRunStore runStore,
                    InMemoryQuestNodeExecutionStore execStore,
                    QuestEntity quest)
        BuildPersistedLinearQuest(int nodeCount = 3)
    {
        var questStore = new InMemoryQuestStore();
        var runStore = new InMemoryQuestRunStore();
        var execStore = new InMemoryQuestNodeExecutionStore();

        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Name = "Linear",
            AvatarId = Guid.NewGuid(),
            Nodes = Enumerable.Range(0, nodeCount).Select(i => new QuestNode
            {
                Id = Guid.NewGuid(),
                Name = $"N{i}",
                IsEntry = i == 0,
                IsTerminal = i == nodeCount - 1,
                NodeType = QuestNodeType.Condition,
                Config = "{}"
            }).ToList()
        };
        for (int i = 0; i < nodeCount - 1; i++)
        {
            quest.Edges.Add(new QuestEdge
            {
                Id = Guid.NewGuid(),
                QuestId = quest.Id,
                SourceNodeId = quest.Nodes[i].Id,
                TargetNodeId = quest.Nodes[i + 1].Id,
                EdgeType = QuestEdgeType.Control
            });
        }

        // Seed the InMemory store.
        questStore.UpsertQuestAsync(quest).GetAwaiter().GetResult();

        var manager = new QuestManager(
            questStore,
            runStore,
            execStore,
            new QuestDagValidator(),
            new QuestNodeHandlerRegistry(Array.Empty<IQuestNodeHandler>()),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty());

        return (manager, questStore, runStore, execStore, quest);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Quest Nodes sub-resource
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ListNodesAsync_ReturnsAllNodes()
    {
        var (manager, _, _, _, quest) = BuildPersistedLinearQuest(nodeCount: 3);

        var result = await manager.ListNodesAsync(quest.Id, quest.AvatarId);

        result.IsError.Should().BeFalse();
        result.Result.Should().HaveCount(3);
        result.Result!.Select(n => n.Id).Should().BeEquivalentTo(quest.Nodes.Select(n => n.Id));
    }

    [Fact]
    public async Task AddNodeAsync_PersistsNewNodeOnQuest()
    {
        var (manager, questStore, _, _, quest) = BuildPersistedLinearQuest(nodeCount: 2);

        var added = await manager.AddNodeAsync(quest.Id, new QuestNodeCreateModel
        {
            Name = "Extra",
            NodeType = QuestNodeType.HolonGet,
            Config = "{}",
            IsEntry = false,
            IsTerminal = false
        }, quest.AvatarId);

        added.IsError.Should().BeFalse();
        added.Result.Should().NotBeNull();
        added.Result!.Name.Should().Be("Extra");
        added.Result.QuestId.Should().Be(quest.Id);

        var reloaded = (await questStore.GetQuestAsync(quest.Id)).Result!;
        reloaded.Nodes.Should().HaveCount(3);
        reloaded.Nodes.Should().Contain(n => n.Id == added.Result.Id);
    }

    [Fact]
    public async Task UpdateNodeAsync_PatchSemantics_OnlyTouchesProvidedFields()
    {
        var (manager, questStore, _, _, quest) = BuildPersistedLinearQuest(nodeCount: 2);
        var nodeId = quest.Nodes[0].Id;
        var originalConfig = quest.Nodes[0].Config;

        // Only Name supplied — Config / IsEntry / IsTerminal must remain untouched.
        var updated = await manager.UpdateNodeAsync(quest.Id, nodeId, new QuestNodeUpdateModel
        {
            Name = "RenamedEntry"
        }, quest.AvatarId);

        updated.IsError.Should().BeFalse();
        var reloaded = (await questStore.GetQuestAsync(quest.Id)).Result!;
        var node = reloaded.Nodes.Single(n => n.Id == nodeId);
        node.Name.Should().Be("RenamedEntry");
        node.Config.Should().Be(originalConfig);
        node.IsEntry.Should().BeTrue("not in patch payload, must stay as seeded");
    }

    [Fact]
    public async Task DeleteNodeAsync_WithReferencingEdges_ReturnsError()
    {
        var (manager, questStore, _, _, quest) = BuildPersistedLinearQuest(nodeCount: 3);

        // Middle node has both incoming and outgoing edges.
        var middleId = quest.Nodes[1].Id;

        var result = await manager.DeleteNodeAsync(quest.Id, middleId, quest.AvatarId);

        result.IsError.Should().BeTrue();
        result.Result.Should().BeFalse();
        result.Message.Should().Contain("edge").And.Contain("reference");

        // Node still present.
        var reloaded = (await questStore.GetQuestAsync(quest.Id)).Result!;
        reloaded.Nodes.Should().Contain(n => n.Id == middleId);
    }

    [Fact]
    public async Task DeleteNodeAsync_AfterRemovingEdges_Succeeds()
    {
        var (manager, questStore, _, _, quest) = BuildPersistedLinearQuest(nodeCount: 3);
        var middleId = quest.Nodes[1].Id;

        // Remove both edges that reference the middle node.
        foreach (var edge in quest.Edges
                     .Where(e => e.SourceNodeId == middleId || e.TargetNodeId == middleId)
                     .ToList())
        {
            var removeResult = await manager.RemoveEdgeAsync(quest.Id, edge.Id, quest.AvatarId);
            removeResult.IsError.Should().BeFalse();
        }

        var delete = await manager.DeleteNodeAsync(quest.Id, middleId, quest.AvatarId);

        delete.IsError.Should().BeFalse();
        delete.Result.Should().BeTrue();
        var reloaded = (await questStore.GetQuestAsync(quest.Id)).Result!;
        reloaded.Nodes.Should().NotContain(n => n.Id == middleId);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Quest Edges sub-resource
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddEdgeAsync_HappyPath_PersistsEdgeAndKeepsDagValid()
    {
        // Three disconnected nodes; we add edges A→B then B→C.
        var questStore = new InMemoryQuestStore();
        var runStore = new InMemoryQuestRunStore();
        var execStore = new InMemoryQuestNodeExecutionStore();

        var nodeA = new QuestNode { Id = Guid.NewGuid(), Name = "A", IsEntry = true, IsTerminal = false, NodeType = QuestNodeType.Condition, Config = "{}" };
        var nodeB = new QuestNode { Id = Guid.NewGuid(), Name = "B", IsEntry = false, IsTerminal = false, NodeType = QuestNodeType.Condition, Config = "{}" };
        var nodeC = new QuestNode { Id = Guid.NewGuid(), Name = "C", IsEntry = false, IsTerminal = true, NodeType = QuestNodeType.Condition, Config = "{}" };

        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Name = "ToWire",
            AvatarId = Guid.NewGuid(),
            Nodes = new List<QuestNode> { nodeA, nodeB, nodeC }
        };
        await questStore.UpsertQuestAsync(quest);

        var manager = new QuestManager(
            questStore, runStore, execStore,
            new QuestDagValidator(),
            new QuestNodeHandlerRegistry(Array.Empty<IQuestNodeHandler>()),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty());

        var first = await manager.AddEdgeAsync(quest.Id, new QuestEdgeAddModel
        {
            SourceNodeId = nodeA.Id, TargetNodeId = nodeB.Id, EdgeType = QuestEdgeType.Control
        }, quest.AvatarId);
        first.IsError.Should().BeFalse();

        var second = await manager.AddEdgeAsync(quest.Id, new QuestEdgeAddModel
        {
            SourceNodeId = nodeB.Id, TargetNodeId = nodeC.Id, EdgeType = QuestEdgeType.Control
        }, quest.AvatarId);
        second.IsError.Should().BeFalse();

        var reloaded = (await questStore.GetQuestAsync(quest.Id)).Result!;
        reloaded.Edges.Should().HaveCount(2);
    }

    [Fact]
    public async Task AddEdgeAsync_CycleIntroduction_RejectsAndDoesNotPersist()
    {
        // 3-node linear A→B→C; attempting to add C→A would form a cycle.
        var (manager, questStore, _, _, quest) = BuildPersistedLinearQuest(nodeCount: 3);

        var cycle = await manager.AddEdgeAsync(quest.Id, new QuestEdgeAddModel
        {
            SourceNodeId = quest.Nodes[2].Id,
            TargetNodeId = quest.Nodes[0].Id,
            EdgeType = QuestEdgeType.Control
        }, quest.AvatarId);

        cycle.IsError.Should().BeTrue();
        cycle.Message.Should().Contain("DAG");

        // Persisted graph still has exactly the two seed edges.
        var reloaded = (await questStore.GetQuestAsync(quest.Id)).Result!;
        reloaded.Edges.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetTopologicalOrderAsync_ReturnsValidatorAssignedOrder()
    {
        var (manager, _, _, _, quest) = BuildPersistedLinearQuest(nodeCount: 3);

        var order = await manager.GetTopologicalOrderAsync(quest.Id, quest.AvatarId);

        order.IsError.Should().BeFalse();
        var ids = order.Result!.ToList();
        ids.Should().HaveCount(3);
        ids[0].Should().Be(quest.Nodes[0].Id, "entry node leads the topological order");
        ids[2].Should().Be(quest.Nodes[2].Id, "terminal node trails the topological order");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Quest Dependencies sub-resource
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddAndRemoveDependency_RoundTrip()
    {
        var (manager, questStore, _, _, quest) = BuildPersistedLinearQuest(nodeCount: 1);
        var otherQuestId = Guid.NewGuid();

        var added = await manager.AddDependencyAsync(quest.Id, new QuestDependencyCreateModel
        {
            DependsOnQuestId = otherQuestId,
            DependencyType = QuestDependencyType.Required
        }, quest.AvatarId);
        added.IsError.Should().BeFalse();

        var reloadedWithDep = (await questStore.GetQuestAsync(quest.Id)).Result!;
        reloadedWithDep.Dependencies.Should().HaveCount(1);

        var removed = await manager.RemoveDependencyAsync(quest.Id, added.Result!.Id, quest.AvatarId);
        removed.IsError.Should().BeFalse();
        removed.Result.Should().BeTrue();

        var reloadedAfter = (await questStore.GetQuestAsync(quest.Id)).Result!;
        reloadedAfter.Dependencies.Should().BeEmpty();
    }

    [Fact]
    public async Task AddDependencyAsync_SelfReference_ReturnsError()
    {
        var (manager, _, _, _, quest) = BuildPersistedLinearQuest(nodeCount: 1);

        var result = await manager.AddDependencyAsync(quest.Id, new QuestDependencyCreateModel
        {
            DependsOnQuestId = quest.Id
        }, quest.AvatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("itself");
    }

    [Fact]
    public async Task CheckDependenciesAsync_UnsatisfiedAndSatisfied_BothSurface()
    {
        var (manager, _, runStore, _, quest) = BuildPersistedLinearQuest(nodeCount: 1);

        // Two cross-quest dependencies — one will be satisfied by a Succeeded
        // run, the other will not.
        var satisfiedQuestId = Guid.NewGuid();
        var unsatisfiedQuestId = Guid.NewGuid();

        var satisfiedDep = await manager.AddDependencyAsync(quest.Id, new QuestDependencyCreateModel
        {
            DependsOnQuestId = satisfiedQuestId
        }, quest.AvatarId);
        var unsatisfiedDep = await manager.AddDependencyAsync(quest.Id, new QuestDependencyCreateModel
        {
            DependsOnQuestId = unsatisfiedQuestId
        }, quest.AvatarId);

        // Seed a Succeeded run for the satisfiedQuest.
        await runStore.CreateAsync(new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = satisfiedQuestId,
            AvatarId = quest.AvatarId,
            Status = QuestRunStatus.Succeeded,
            StartedAt = DateTime.UtcNow,
            EndedAt = DateTime.UtcNow
        });

        // Seed only an in-flight Running run for the unsatisfiedQuest — does
        // not count as satisfied.
        await runStore.CreateAsync(new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = unsatisfiedQuestId,
            AvatarId = quest.AvatarId,
            Status = QuestRunStatus.Running,
            StartedAt = DateTime.UtcNow
        });

        var check = await manager.CheckDependenciesAsync(quest.Id, quest.AvatarId);

        check.IsError.Should().BeFalse();
        check.Result!.AllSatisfied.Should().BeFalse();
        check.Result.UnsatisfiedDependencyIds.Should().ContainSingle()
             .Which.Should().Be(unsatisfiedDep.Result!.Id);
    }

    // ═══════════════════════════════════════════════════════════════════
    // QuestRun read surface
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetRunAsync_AndListRunsByQuest_RoundTrip()
    {
        var (manager, _, runStore, _, quest) = BuildPersistedLinearQuest(nodeCount: 1);

        var run = new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            AvatarId = quest.AvatarId,
            Status = QuestRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        await runStore.CreateAsync(run);

        var getRun = await manager.GetRunAsync(run.Id, quest.AvatarId);
        getRun.IsError.Should().BeFalse();
        getRun.Result!.Id.Should().Be(run.Id);

        var list = await manager.ListRunsByQuestAsync(quest.Id, quest.AvatarId);
        list.IsError.Should().BeFalse();
        list.Result!.Should().ContainSingle().Which.Id.Should().Be(run.Id);
    }

    [Fact]
    public async Task GetExecutionStateAsync_AggregatesCountsFromExecutions()
    {
        var (manager, _, runStore, execStore, quest) = BuildPersistedLinearQuest(nodeCount: 3);

        var run = new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            AvatarId = quest.AvatarId,
            Status = QuestRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        await runStore.CreateAsync(run);

        // 1 Succeeded, 1 Failed, 1 Running.
        await execStore.CreateAsync(new QuestNodeExecution
        {
            Id = Guid.NewGuid(), RunId = run.Id, NodeId = quest.Nodes[0].Id,
            State = QuestNodeState.Succeeded, StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow
        });
        await execStore.CreateAsync(new QuestNodeExecution
        {
            Id = Guid.NewGuid(), RunId = run.Id, NodeId = quest.Nodes[1].Id,
            State = QuestNodeState.Failed, StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow,
            Error = "boom"
        });
        await execStore.CreateAsync(new QuestNodeExecution
        {
            Id = Guid.NewGuid(), RunId = run.Id, NodeId = quest.Nodes[2].Id,
            State = QuestNodeState.Running, StartedAt = DateTime.UtcNow
        });

        var state = await manager.GetExecutionStateAsync(run.Id, quest.AvatarId);

        state.IsError.Should().BeFalse();
        state.Result!.RunId.Should().Be(run.Id);
        state.Result.QuestId.Should().Be(quest.Id);
        state.Result.TotalNodes.Should().Be(3);
        state.Result.CompletedNodes.Should().Be(1);
        state.Result.FailedNodes.Should().Be(1);
        state.Result.PendingNodes.Should().Be(1, "Running is grouped with Pending as in-flight");
        state.Result.NodeExecutions.Should().HaveCount(3);
    }

    [Fact]
    public async Task MarkRunCompletedAsync_WithInFlightExecutions_ReturnsError()
    {
        var (manager, _, runStore, execStore, quest) = BuildPersistedLinearQuest(nodeCount: 2);

        var run = new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            AvatarId = quest.AvatarId,
            Status = QuestRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        await runStore.CreateAsync(run);

        // One Succeeded, one still Pending.
        await execStore.CreateAsync(new QuestNodeExecution
        {
            Id = Guid.NewGuid(), RunId = run.Id, NodeId = quest.Nodes[0].Id,
            State = QuestNodeState.Succeeded, StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow
        });
        await execStore.CreateAsync(new QuestNodeExecution
        {
            Id = Guid.NewGuid(), RunId = run.Id, NodeId = quest.Nodes[1].Id,
            State = QuestNodeState.Pending, StartedAt = DateTime.UtcNow
        });

        var result = await manager.MarkRunCompletedAsync(run.Id, quest.AvatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("in flight");

        var reloaded = (await runStore.GetByIdAsync(run.Id)).Result!;
        reloaded.Status.Should().Be(QuestRunStatus.Running, "guard prevents the terminal transition");
    }

    [Fact]
    public async Task MarkRunCompletedAsync_AllTerminal_TransitionsToSucceeded()
    {
        var (manager, _, runStore, execStore, quest) = BuildPersistedLinearQuest(nodeCount: 2);

        var run = new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            AvatarId = quest.AvatarId,
            Status = QuestRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        await runStore.CreateAsync(run);

        await execStore.CreateAsync(new QuestNodeExecution
        {
            Id = Guid.NewGuid(), RunId = run.Id, NodeId = quest.Nodes[0].Id,
            State = QuestNodeState.Succeeded, StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow
        });
        await execStore.CreateAsync(new QuestNodeExecution
        {
            Id = Guid.NewGuid(), RunId = run.Id, NodeId = quest.Nodes[1].Id,
            State = QuestNodeState.Succeeded, StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow
        });

        var result = await manager.MarkRunCompletedAsync(run.Id, quest.AvatarId);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(QuestRunStatus.Succeeded);
        result.Result.EndedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkRunCompletedAsync_AnyFailedExecution_TransitionsToFailed()
    {
        var (manager, _, runStore, execStore, quest) = BuildPersistedLinearQuest(nodeCount: 2);

        var run = new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            AvatarId = quest.AvatarId,
            Status = QuestRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        await runStore.CreateAsync(run);

        await execStore.CreateAsync(new QuestNodeExecution
        {
            Id = Guid.NewGuid(), RunId = run.Id, NodeId = quest.Nodes[0].Id,
            State = QuestNodeState.Succeeded, StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow
        });
        await execStore.CreateAsync(new QuestNodeExecution
        {
            Id = Guid.NewGuid(), RunId = run.Id, NodeId = quest.Nodes[1].Id,
            State = QuestNodeState.Failed, StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow,
            Error = "boom"
        });

        var result = await manager.MarkRunCompletedAsync(run.Id, quest.AvatarId);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(QuestRunStatus.Failed,
            "any failed execution flips the supervisor-completed run to Failed (mirrors ExecuteAsync derivation)");
    }

    [Fact]
    public async Task MarkRunCompletedAsync_NonRunningRun_ReturnsError()
    {
        var (manager, _, runStore, _, quest) = BuildPersistedLinearQuest(nodeCount: 1);

        var run = new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            AvatarId = quest.AvatarId,
            Status = QuestRunStatus.Succeeded,
            StartedAt = DateTime.UtcNow,
            EndedAt = DateTime.UtcNow
        };
        await runStore.CreateAsync(run);

        var result = await manager.MarkRunCompletedAsync(run.Id, quest.AvatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Running");
    }
}
