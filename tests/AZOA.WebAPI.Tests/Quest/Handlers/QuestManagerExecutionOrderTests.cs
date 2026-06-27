using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Services.Quest.Handlers;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Tests.Quest.Handlers;

/// <summary>
/// Guards the ExecutionOrder dedupe: QuestManager deleted its own
/// topological-order reassignment; QuestDagValidator is the single authority
/// and mutates node.ExecutionOrder in-place. ValidateDAGAsync must still
/// persist a graph whose nodes carry the validator-assigned order.
/// </summary>
public class QuestManagerExecutionOrderTests
{
    [Fact]
    public async Task ValidateDAGAsync_SetsExecutionOrderViaValidator_AndPersists()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var cId = Guid.NewGuid();
        var questId = Guid.NewGuid();

        var quest = new QuestEntity
        {
            Id = questId,
            Name = "Linear",
            AvatarId = Guid.NewGuid(),
            Nodes = new List<QuestNode>
            {
                new() { Id = aId, Name = "A", IsEntry = true, IsTerminal = false },
                new() { Id = bId, Name = "B", IsEntry = false, IsTerminal = false },
                new() { Id = cId, Name = "C", IsEntry = false, IsTerminal = true }
            },
            Edges = new List<QuestEdge>
            {
                new() { Id = Guid.NewGuid(), SourceNodeId = aId, TargetNodeId = bId },
                new() { Id = Guid.NewGuid(), SourceNodeId = bId, TargetNodeId = cId }
            }
        };

        QuestEntity? persisted = null;
        var store = new Mock<IQuestStore>();
        store.Setup(s => s.GetQuestAsync(questId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new AZOAResult<QuestEntity> { Result = quest });
        store.Setup(s => s.UpsertQuestAsync(It.IsAny<QuestEntity>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((QuestEntity q, CancellationToken _) => { persisted = q; return new AZOAResult<QuestEntity> { Result = q }; });

        // Real validator — the single ExecutionOrder authority. The InMemory
        // run + execution stores are required by the manager's constructor
        // but are unused for the pure-validation path under test.
        var manager = new QuestManager(
            store.Object,
            new InMemoryQuestRunStore(),
            new InMemoryQuestNodeExecutionStore(),
            new QuestDagValidator(),
            new QuestNodeHandlerRegistry(Array.Empty<IQuestNodeHandler>()),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning());

        var result = await manager.ValidateDAGAsync(questId);

        result.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();

        var nodeA = quest.Nodes.First(n => n.Id == aId);
        var nodeB = quest.Nodes.First(n => n.Id == bId);
        var nodeC = quest.Nodes.First(n => n.Id == cId);
        nodeA.ExecutionOrder.Should().BeLessThan(nodeB.ExecutionOrder);
        nodeB.ExecutionOrder.Should().BeLessThan(nodeC.ExecutionOrder);

        // The persisted graph carries the validator-assigned order.
        persisted.Should().NotBeNull();
        persisted!.Nodes.Select(n => n.ExecutionOrder).Distinct().Should().HaveCount(3);
        store.Verify(s => s.UpsertQuestAsync(It.IsAny<QuestEntity>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WritesPerRunPerNodeExecution_NotQuestNode()
    {
        // After quest-temporal-fork-model, ExecuteAsync produces a QuestRun
        // and per-(run, node) QuestNodeExecution rows. The QuestNode definition
        // is never mutated — this is the central invariant of the rewrite.
        var nodeId = Guid.NewGuid();
        var questId = Guid.NewGuid();

        var quest = new QuestEntity
        {
            Id = questId,
            Name = "Single",
            AvatarId = Guid.NewGuid(),
            Nodes = new List<QuestNode>
            {
                new() { Id = nodeId, Name = "Only", IsEntry = true, IsTerminal = true, NodeType = QuestNodeType.Condition, Config = "{}" }
            },
            Edges = new List<QuestEdge>()
        };

        var store = new Mock<IQuestStore>();
        store.Setup(s => s.GetQuestAsync(questId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new AZOAResult<QuestEntity> { Result = quest });
        store.Setup(s => s.UpsertQuestAsync(It.IsAny<QuestEntity>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((QuestEntity q, CancellationToken _) => new AZOAResult<QuestEntity> { Result = q });

        var runStore = new InMemoryQuestRunStore();
        var execStore = new InMemoryQuestNodeExecutionStore();
        var manager = new QuestManager(
            store.Object,
            runStore,
            execStore,
            new QuestDagValidator(),
            new QuestNodeHandlerRegistry(new IQuestNodeHandler[] { new ConditionNodeHandler() }),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning());

        var result = await manager.ExecuteAsync(questId, quest.AvatarId);

        result.IsError.Should().BeFalse();
        result.Result.Should().NotBeNull();
        result.Result!.Status.Should().Be(QuestRunStatus.Succeeded);

        // Per-(run, node) execution row exists and is Succeeded.
        var exec = (await execStore.GetByRunAndNodeAsync(result.Result.Id, nodeId)).Result;
        exec.Should().NotBeNull();
        exec!.State.Should().Be(QuestNodeState.Succeeded);
        exec.RunId.Should().Be(result.Result.Id);
        exec.NodeId.Should().Be(nodeId);
    }
}
