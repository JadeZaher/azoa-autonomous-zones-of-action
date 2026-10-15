using FluentAssertions;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions;

/// <summary>
/// Manager-level unit tests for the post-hoc sub-resource CRUD endpoints
/// (Nodes / Edges / Dependencies) plus the ActionRun read surface added by the
/// action-api endpoint gap fill (RUNBOOK section 5 Phase F).
///
/// <para>
/// Uses real InMemory stores (rather than Mocks) so action mutations actually
/// persist between manager calls — many of these methods are
/// load-then-mutate-then-save round trips and a Mocked GetActionAsync would
/// return a stale snapshot on the second read.
/// </para>
/// </summary>
public class ActionManagerSubResourceTests
{
    // ─── Test scaffolding ───

    private static (ActionManager manager,
                    InMemoryActionStore actionStore,
                    InMemoryActionRunStore runStore,
                    InMemoryActionNodeExecutionStore execStore,
                    ActionEntity action)
        BuildPersistedLinearAction(int nodeCount = 3)
    {
        var actionStore = new InMemoryActionStore();
        var runStore = new InMemoryActionRunStore();
        var execStore = new InMemoryActionNodeExecutionStore();

        var action = new ActionEntity
        {
            Id = Guid.NewGuid(),
            Name = "Linear",
            UserId = Guid.NewGuid(),
            Nodes = Enumerable.Range(0, nodeCount).Select(i => new ActionNode
            {
                Id = Guid.NewGuid(),
                Name = $"N{i}",
                IsEntry = i == 0,
                IsTerminal = i == nodeCount - 1,
                NodeType = ActionNodeType.Condition,
                Config = "{}"
            }).ToList()
        };
        for (int i = 0; i < nodeCount - 1; i++)
        {
            action.Edges.Add(new ActionEdge
            {
                Id = Guid.NewGuid(),
                ActionId = action.Id,
                SourceNodeId = action.Nodes[i].Id,
                TargetNodeId = action.Nodes[i + 1].Id,
                EdgeType = ActionEdgeType.Control
            });
        }

        // Seed the InMemory store.
        actionStore.UpsertActionAsync(action).GetAwaiter().GetResult();

        var manager = new ActionManager(
            actionStore,
            runStore,
            execStore,
            new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(Array.Empty<IActionNodeHandler>()),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        return (manager, actionStore, runStore, execStore, action);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Action Nodes sub-resource
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ListNodesAsync_ReturnsAllNodes()
    {
        var (manager, _, _, _, action) = BuildPersistedLinearAction(nodeCount: 3);

        var result = await manager.ListNodesAsync(action.Id, action.UserId);

        result.IsError.Should().BeFalse();
        result.Result.Should().HaveCount(3);
        result.Result!.Select(n => n.Id).Should().BeEquivalentTo(action.Nodes.Select(n => n.Id));
    }

    [Fact]
    public async Task AddNodeAsync_PersistsNewNodeOnAction()
    {
        var (manager, actionStore, _, _, action) = BuildPersistedLinearAction(nodeCount: 2);

        var added = await manager.AddNodeAsync(action.Id, new ActionNodeCreateModel
        {
            Name = "Extra",
            NodeType = ActionNodeType.HolonGet,
            Config = "{}",
            IsEntry = false,
            IsTerminal = false
        }, action.UserId);

        added.IsError.Should().BeFalse();
        added.Result.Should().NotBeNull();
        added.Result!.Name.Should().Be("Extra");
        added.Result.ActionId.Should().Be(action.Id);

        var reloaded = (await actionStore.GetActionAsync(action.Id)).Result!;
        reloaded.Nodes.Should().HaveCount(3);
        reloaded.Nodes.Should().Contain(n => n.Id == added.Result.Id);
    }

    [Fact]
    public async Task UpdateNodeAsync_PatchSemantics_OnlyTouchesProvidedFields()
    {
        var (manager, actionStore, _, _, action) = BuildPersistedLinearAction(nodeCount: 2);
        var nodeId = action.Nodes[0].Id;
        var originalConfig = action.Nodes[0].Config;

        // Only Name supplied — Config / IsEntry / IsTerminal must remain untouched.
        var updated = await manager.UpdateNodeAsync(action.Id, nodeId, new ActionNodeUpdateModel
        {
            Name = "RenamedEntry"
        }, action.UserId);

        updated.IsError.Should().BeFalse();
        var reloaded = (await actionStore.GetActionAsync(action.Id)).Result!;
        var node = reloaded.Nodes.Single(n => n.Id == nodeId);
        node.Name.Should().Be("RenamedEntry");
        node.Config.Should().Be(originalConfig);
        node.IsEntry.Should().BeTrue("not in patch payload, must stay as seeded");
    }

    [Fact]
    public async Task DeleteNodeAsync_WithReferencingEdges_ReturnsError()
    {
        var (manager, actionStore, _, _, action) = BuildPersistedLinearAction(nodeCount: 3);

        // Middle node has both incoming and outgoing edges.
        var middleId = action.Nodes[1].Id;

        var result = await manager.DeleteNodeAsync(action.Id, middleId, action.UserId);

        result.IsError.Should().BeTrue();
        result.Result.Should().BeFalse();
        result.Message.Should().Contain("edge").And.Contain("reference");

        // Node still present.
        var reloaded = (await actionStore.GetActionAsync(action.Id)).Result!;
        reloaded.Nodes.Should().Contain(n => n.Id == middleId);
    }

    [Fact]
    public async Task DeleteNodeAsync_AfterRemovingEdges_Succeeds()
    {
        var (manager, actionStore, _, _, action) = BuildPersistedLinearAction(nodeCount: 3);
        var middleId = action.Nodes[1].Id;

        // Remove both edges that reference the middle node.
        foreach (var edge in action.Edges
                     .Where(e => e.SourceNodeId == middleId || e.TargetNodeId == middleId)
                     .ToList())
        {
            var removeResult = await manager.RemoveEdgeAsync(action.Id, edge.Id, action.UserId);
            removeResult.IsError.Should().BeFalse();
        }

        var delete = await manager.DeleteNodeAsync(action.Id, middleId, action.UserId);

        delete.IsError.Should().BeFalse();
        delete.Result.Should().BeTrue();
        var reloaded = (await actionStore.GetActionAsync(action.Id)).Result!;
        reloaded.Nodes.Should().NotContain(n => n.Id == middleId);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Action Edges sub-resource
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddEdgeAsync_HappyPath_PersistsEdgeAndKeepsDagValid()
    {
        // Three disconnected nodes; we add edges A→B then B→C.
        var actionStore = new InMemoryActionStore();
        var runStore = new InMemoryActionRunStore();
        var execStore = new InMemoryActionNodeExecutionStore();

        var nodeA = new ActionNode { Id = Guid.NewGuid(), Name = "A", IsEntry = true, IsTerminal = false, NodeType = ActionNodeType.Condition, Config = "{}" };
        var nodeB = new ActionNode { Id = Guid.NewGuid(), Name = "B", IsEntry = false, IsTerminal = false, NodeType = ActionNodeType.Condition, Config = "{}" };
        var nodeC = new ActionNode { Id = Guid.NewGuid(), Name = "C", IsEntry = false, IsTerminal = true, NodeType = ActionNodeType.Condition, Config = "{}" };

        var action = new ActionEntity
        {
            Id = Guid.NewGuid(),
            Name = "ToWire",
            UserId = Guid.NewGuid(),
            Nodes = new List<ActionNode> { nodeA, nodeB, nodeC }
        };
        await actionStore.UpsertActionAsync(action);

        var manager = new ActionManager(
            actionStore, runStore, execStore,
            new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(Array.Empty<IActionNodeHandler>()),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var first = await manager.AddEdgeAsync(action.Id, new ActionEdgeAddModel
        {
            SourceNodeId = nodeA.Id, TargetNodeId = nodeB.Id, EdgeType = ActionEdgeType.Control
        }, action.UserId);
        first.IsError.Should().BeFalse();

        var second = await manager.AddEdgeAsync(action.Id, new ActionEdgeAddModel
        {
            SourceNodeId = nodeB.Id, TargetNodeId = nodeC.Id, EdgeType = ActionEdgeType.Control
        }, action.UserId);
        second.IsError.Should().BeFalse();

        var reloaded = (await actionStore.GetActionAsync(action.Id)).Result!;
        reloaded.Edges.Should().HaveCount(2);
    }

    [Fact]
    public async Task AddEdgeAsync_CycleIntroduction_RejectsAndDoesNotPersist()
    {
        // 3-node linear A→B→C; attempting to add C→A would form a cycle.
        var (manager, actionStore, _, _, action) = BuildPersistedLinearAction(nodeCount: 3);

        var cycle = await manager.AddEdgeAsync(action.Id, new ActionEdgeAddModel
        {
            SourceNodeId = action.Nodes[2].Id,
            TargetNodeId = action.Nodes[0].Id,
            EdgeType = ActionEdgeType.Control
        }, action.UserId);

        cycle.IsError.Should().BeTrue();
        cycle.Message.Should().Contain("DAG");

        // Persisted graph still has exactly the two seed edges.
        var reloaded = (await actionStore.GetActionAsync(action.Id)).Result!;
        reloaded.Edges.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetTopologicalOrderAsync_ReturnsValidatorAssignedOrder()
    {
        var (manager, _, _, _, action) = BuildPersistedLinearAction(nodeCount: 3);

        var order = await manager.GetTopologicalOrderAsync(action.Id, action.UserId);

        order.IsError.Should().BeFalse();
        var ids = order.Result!.ToList();
        ids.Should().HaveCount(3);
        ids[0].Should().Be(action.Nodes[0].Id, "entry node leads the topological order");
        ids[2].Should().Be(action.Nodes[2].Id, "terminal node trails the topological order");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Action Dependencies sub-resource
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddAndRemoveDependency_RoundTrip()
    {
        var (manager, actionStore, _, _, action) = BuildPersistedLinearAction(nodeCount: 1);
        var otherActionId = Guid.NewGuid();

        var added = await manager.AddDependencyAsync(action.Id, new ActionDependencyCreateModel
        {
            DependsOnActionId = otherActionId,
            DependencyType = ActionDependencyType.Required
        }, action.UserId);
        added.IsError.Should().BeFalse();

        var reloadedWithDep = (await actionStore.GetActionAsync(action.Id)).Result!;
        reloadedWithDep.Dependencies.Should().HaveCount(1);

        var removed = await manager.RemoveDependencyAsync(action.Id, added.Result!.Id, action.UserId);
        removed.IsError.Should().BeFalse();
        removed.Result.Should().BeTrue();

        var reloadedAfter = (await actionStore.GetActionAsync(action.Id)).Result!;
        reloadedAfter.Dependencies.Should().BeEmpty();
    }

    [Fact]
    public async Task AddDependencyAsync_SelfReference_ReturnsError()
    {
        var (manager, _, _, _, action) = BuildPersistedLinearAction(nodeCount: 1);

        var result = await manager.AddDependencyAsync(action.Id, new ActionDependencyCreateModel
        {
            DependsOnActionId = action.Id
        }, action.UserId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("itself");
    }

    [Fact]
    public async Task CheckDependenciesAsync_UnsatisfiedAndSatisfied_BothSurface()
    {
        var (manager, _, runStore, _, action) = BuildPersistedLinearAction(nodeCount: 1);

        // Two cross-action dependencies — one will be satisfied by a Succeeded
        // run, the other will not.
        var satisfiedActionId = Guid.NewGuid();
        var unsatisfiedActionId = Guid.NewGuid();

        var satisfiedDep = await manager.AddDependencyAsync(action.Id, new ActionDependencyCreateModel
        {
            DependsOnActionId = satisfiedActionId
        }, action.UserId);
        var unsatisfiedDep = await manager.AddDependencyAsync(action.Id, new ActionDependencyCreateModel
        {
            DependsOnActionId = unsatisfiedActionId
        }, action.UserId);

        // Seed a Succeeded run for the satisfiedAction.
        await runStore.CreateAsync(new ActionRun
        {
            Id = Guid.NewGuid(),
            ActionId = satisfiedActionId,
            UserId = action.UserId,
            Status = ActionRunStatus.Succeeded,
            StartedAt = DateTime.UtcNow,
            EndedAt = DateTime.UtcNow
        });

        // Seed only an in-flight Running run for the unsatisfiedAction — does
        // not count as satisfied.
        await runStore.CreateAsync(new ActionRun
        {
            Id = Guid.NewGuid(),
            ActionId = unsatisfiedActionId,
            UserId = action.UserId,
            Status = ActionRunStatus.Running,
            StartedAt = DateTime.UtcNow
        });

        var check = await manager.CheckDependenciesAsync(action.Id, action.UserId);

        check.IsError.Should().BeFalse();
        check.Result!.AllSatisfied.Should().BeFalse();
        check.Result.UnsatisfiedDependencyIds.Should().ContainSingle()
             .Which.Should().Be(unsatisfiedDep.Result!.Id);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ActionRun read surface
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetRunAsync_AndListRunsByAction_RoundTrip()
    {
        var (manager, _, runStore, _, action) = BuildPersistedLinearAction(nodeCount: 1);

        var run = new ActionRun
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            UserId = action.UserId,
            Status = ActionRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        await runStore.CreateAsync(run);

        var getRun = await manager.GetRunAsync(run.Id, action.UserId);
        getRun.IsError.Should().BeFalse();
        getRun.Result!.Id.Should().Be(run.Id);

        var list = await manager.ListRunsByActionAsync(action.Id, action.UserId);
        list.IsError.Should().BeFalse();
        list.Result!.Should().ContainSingle().Which.Id.Should().Be(run.Id);
    }

    [Fact]
    public async Task GetExecutionStateAsync_AggregatesCountsFromExecutions()
    {
        var (manager, _, runStore, execStore, action) = BuildPersistedLinearAction(nodeCount: 3);

        var run = new ActionRun
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            UserId = action.UserId,
            Status = ActionRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        await runStore.CreateAsync(run);

        // 1 Succeeded, 1 Failed, 1 Running.
        await execStore.CreateAsync(new ActionNodeExecution
        {
            Id = Guid.NewGuid(), RunId = run.Id, NodeId = action.Nodes[0].Id,
            State = ActionNodeState.Succeeded, StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow
        });
        await execStore.CreateAsync(new ActionNodeExecution
        {
            Id = Guid.NewGuid(), RunId = run.Id, NodeId = action.Nodes[1].Id,
            State = ActionNodeState.Failed, StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow,
            Error = "boom"
        });
        await execStore.CreateAsync(new ActionNodeExecution
        {
            Id = Guid.NewGuid(), RunId = run.Id, NodeId = action.Nodes[2].Id,
            State = ActionNodeState.Running, StartedAt = DateTime.UtcNow
        });

        var state = await manager.GetExecutionStateAsync(run.Id, action.UserId);

        state.IsError.Should().BeFalse();
        state.Result!.RunId.Should().Be(run.Id);
        state.Result.ActionId.Should().Be(action.Id);
        state.Result.TotalNodes.Should().Be(3);
        state.Result.CompletedNodes.Should().Be(1);
        state.Result.FailedNodes.Should().Be(1);
        state.Result.PendingNodes.Should().Be(1, "Running is grouped with Pending as in-flight");
        state.Result.NodeExecutions.Should().HaveCount(3);
    }

    [Fact]
    public async Task MarkRunCompletedAsync_WithInFlightExecutions_ReturnsError()
    {
        var (manager, _, runStore, execStore, action) = BuildPersistedLinearAction(nodeCount: 2);

        var run = new ActionRun
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            UserId = action.UserId,
            Status = ActionRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        await runStore.CreateAsync(run);

        // One Succeeded, one still Pending.
        await execStore.CreateAsync(new ActionNodeExecution
        {
            Id = Guid.NewGuid(), RunId = run.Id, NodeId = action.Nodes[0].Id,
            State = ActionNodeState.Succeeded, StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow
        });
        await execStore.CreateAsync(new ActionNodeExecution
        {
            Id = Guid.NewGuid(), RunId = run.Id, NodeId = action.Nodes[1].Id,
            State = ActionNodeState.Pending, StartedAt = DateTime.UtcNow
        });

        var result = await manager.MarkRunCompletedAsync(run.Id, action.UserId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("in flight");

        var reloaded = (await runStore.GetByIdAsync(run.Id)).Result!;
        reloaded.Status.Should().Be(ActionRunStatus.Running, "guard prevents the terminal transition");
    }

    [Fact]
    public async Task MarkRunCompletedAsync_AllTerminal_TransitionsToSucceeded()
    {
        var (manager, _, runStore, execStore, action) = BuildPersistedLinearAction(nodeCount: 2);

        var run = new ActionRun
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            UserId = action.UserId,
            Status = ActionRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        await runStore.CreateAsync(run);

        await execStore.CreateAsync(new ActionNodeExecution
        {
            Id = Guid.NewGuid(), RunId = run.Id, NodeId = action.Nodes[0].Id,
            State = ActionNodeState.Succeeded, StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow
        });
        await execStore.CreateAsync(new ActionNodeExecution
        {
            Id = Guid.NewGuid(), RunId = run.Id, NodeId = action.Nodes[1].Id,
            State = ActionNodeState.Succeeded, StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow
        });

        var result = await manager.MarkRunCompletedAsync(run.Id, action.UserId);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(ActionRunStatus.Succeeded);
        result.Result.EndedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkRunCompletedAsync_AnyFailedExecution_TransitionsToFailed()
    {
        var (manager, _, runStore, execStore, action) = BuildPersistedLinearAction(nodeCount: 2);

        var run = new ActionRun
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            UserId = action.UserId,
            Status = ActionRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        await runStore.CreateAsync(run);

        await execStore.CreateAsync(new ActionNodeExecution
        {
            Id = Guid.NewGuid(), RunId = run.Id, NodeId = action.Nodes[0].Id,
            State = ActionNodeState.Succeeded, StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow
        });
        await execStore.CreateAsync(new ActionNodeExecution
        {
            Id = Guid.NewGuid(), RunId = run.Id, NodeId = action.Nodes[1].Id,
            State = ActionNodeState.Failed, StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow,
            Error = "boom"
        });

        var result = await manager.MarkRunCompletedAsync(run.Id, action.UserId);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(ActionRunStatus.Failed,
            "any failed execution flips the supervisor-completed run to Failed (mirrors ExecuteAsync derivation)");
    }

    [Fact]
    public async Task MarkRunCompletedAsync_NonRunningRun_ReturnsError()
    {
        var (manager, _, runStore, _, action) = BuildPersistedLinearAction(nodeCount: 1);

        var run = new ActionRun
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            UserId = action.UserId,
            Status = ActionRunStatus.Succeeded,
            StartedAt = DateTime.UtcNow,
            EndedAt = DateTime.UtcNow
        };
        await runStore.CreateAsync(run);

        var result = await manager.MarkRunCompletedAsync(run.Id, action.UserId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Running");
    }
}
