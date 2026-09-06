using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Handlers;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions.Handlers;

/// <summary>
/// Guards the ExecutionOrder dedupe: ActionManager deleted its own
/// topological-order reassignment; ActionDagValidator is the single authority
/// and mutates node.ExecutionOrder in-place. ValidateDAGAsync must still
/// persist a graph whose nodes carry the validator-assigned order.
/// </summary>
public class ActionManagerExecutionOrderTests
{
    [Fact]
    public async Task ValidateDAGAsync_SetsExecutionOrderViaValidator_AndPersists()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var cId = Guid.NewGuid();
        var actionId = Guid.NewGuid();

        var action = new ActionEntity
        {
            Id = actionId,
            Name = "Linear",
            UserId = Guid.NewGuid(),
            Nodes = new List<ActionNode>
            {
                new() { Id = aId, Name = "A", IsEntry = true, IsTerminal = false },
                new() { Id = bId, Name = "B", IsEntry = false, IsTerminal = false },
                new() { Id = cId, Name = "C", IsEntry = false, IsTerminal = true }
            },
            Edges = new List<ActionEdge>
            {
                new() { Id = Guid.NewGuid(), SourceNodeId = aId, TargetNodeId = bId },
                new() { Id = Guid.NewGuid(), SourceNodeId = bId, TargetNodeId = cId }
            }
        };

        ActionEntity? persisted = null;
        var store = new Mock<IActionStore>();
        store.Setup(s => s.GetActionAsync(actionId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new AZOAResult<ActionEntity> { Result = action });
        store.Setup(s => s.UpsertActionAsync(It.IsAny<ActionEntity>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((ActionEntity q, CancellationToken _) => { persisted = q; return new AZOAResult<ActionEntity> { Result = q }; });

        // Real validator — the single ExecutionOrder authority. The InMemory
        // run + execution stores are required by the manager's constructor
        // but are unused for the pure-validation path under test.
        var manager = new ActionManager(
            store.Object,
            new InMemoryActionRunStore(),
            new InMemoryActionNodeExecutionStore(),
            new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(Array.Empty<IActionNodeHandler>()),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var result = await manager.ValidateDAGAsync(actionId);

        result.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();

        var nodeA = action.Nodes.First(n => n.Id == aId);
        var nodeB = action.Nodes.First(n => n.Id == bId);
        var nodeC = action.Nodes.First(n => n.Id == cId);
        nodeA.ExecutionOrder.Should().BeLessThan(nodeB.ExecutionOrder);
        nodeB.ExecutionOrder.Should().BeLessThan(nodeC.ExecutionOrder);

        // The persisted graph carries the validator-assigned order.
        persisted.Should().NotBeNull();
        persisted!.Nodes.Select(n => n.ExecutionOrder).Distinct().Should().HaveCount(3);
        store.Verify(s => s.UpsertActionAsync(It.IsAny<ActionEntity>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WritesPerRunPerNodeExecution_NotActionNode()
    {
        // After action-temporal-fork-model, ExecuteAsync produces an ActionRun
        // and per-(run, node) ActionNodeExecution rows. The ActionNode definition
        // is never mutated — this is the central invariant of the rewrite.
        var nodeId = Guid.NewGuid();
        var actionId = Guid.NewGuid();

        var action = new ActionEntity
        {
            Id = actionId,
            Name = "Single",
            UserId = Guid.NewGuid(),
            Status = ActionStatus.Active,   // B6: must be Active to execute
            Nodes = new List<ActionNode>
            {
                new() { Id = nodeId, Name = "Only", IsEntry = true, IsTerminal = true, NodeType = ActionNodeType.Condition, Config = "{}" }
            },
            Edges = new List<ActionEdge>()
        };

        var store = new Mock<IActionStore>();
        store.Setup(s => s.GetActionAsync(actionId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new AZOAResult<ActionEntity> { Result = action });
        store.Setup(s => s.UpsertActionAsync(It.IsAny<ActionEntity>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((ActionEntity q, CancellationToken _) => new AZOAResult<ActionEntity> { Result = q });
        // F6: the run-start version-confirm sees an unchanged definition here.
        store.Setup(s => s.TryConfirmActionStateAsync(It.IsAny<Guid>(), It.IsAny<ActionStatus>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(1);

        var runStore = new InMemoryActionRunStore();
        var execStore = new InMemoryActionNodeExecutionStore();
        var manager = new ActionManager(
            store.Object,
            runStore,
            execStore,
            new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(new IActionNodeHandler[] { new ConditionNodeHandler() }),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var result = await manager.ExecuteAsync(actionId, action.UserId);

        result.IsError.Should().BeFalse();
        result.Result.Should().NotBeNull();
        result.Result!.Status.Should().Be(ActionRunStatus.Succeeded);

        // Per-(run, node) execution row exists and is Succeeded.
        var exec = (await execStore.GetByRunAndNodeAsync(result.Result.Id, nodeId)).Result;
        exec.Should().NotBeNull();
        exec!.State.Should().Be(ActionNodeState.Succeeded);
        exec.RunId.Should().Be(result.Result.Id);
        exec.NodeId.Should().Be(nodeId);
    }
}
