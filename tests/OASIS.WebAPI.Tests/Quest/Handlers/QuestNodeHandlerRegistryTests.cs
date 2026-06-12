using FluentAssertions;
using Moq;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.QuestExecution;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Managers;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Responses;
using OASIS.WebAPI.Providers.Stores;
using OASIS.WebAPI.Services.Quest;
using Xunit;
using QuestEntity = OASIS.WebAPI.Models.Quest.Quest;

namespace OASIS.WebAPI.Tests.Quest.Handlers;

public class QuestNodeHandlerRegistryTests
{
    private static IQuestNodeHandler Handler(QuestNodeType type)
    {
        var m = new Mock<IQuestNodeHandler>();
        m.SetupGet(h => h.NodeType).Returns(type);
        return m.Object;
    }

    [Fact]
    public void Registry_ResolvesRegisteredHandlerByType()
    {
        var holonGet = Handler(QuestNodeType.HolonGet);
        var registry = new QuestNodeHandlerRegistry(new[] { holonGet, Handler(QuestNodeType.NftMint) });

        registry.TryGet(QuestNodeType.HolonGet, out var resolved).Should().BeTrue();
        resolved.Should().BeSameAs(holonGet);
    }

    [Fact]
    public void Registry_UnknownType_TryGetReturnsFalse()
    {
        var registry = new QuestNodeHandlerRegistry(new[] { Handler(QuestNodeType.HolonGet) });

        registry.TryGet(QuestNodeType.ComposeOutputs, out var resolved).Should().BeFalse();
        resolved.Should().BeNull();
    }

    [Fact]
    public void Registry_DuplicateNodeType_Throws()
    {
        var act = () => new QuestNodeHandlerRegistry(new[]
        {
            Handler(QuestNodeType.HolonGet),
            Handler(QuestNodeType.HolonGet)
        });

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Duplicate quest node handler for HolonGet*");
    }

    [Fact]
    public async Task QuestManager_UnknownNodeType_RegistryMiss_FailsExecutionRow()
    {
        // Registry with no handler for the node's type => TryGet false =>
        // QuestManager records a Failed QuestNodeExecution with the
        // "Unsupported node type" error message (preserves the former
        // default-case Fail behaviour, now keyed by (runId, nodeId)).
        var emptyRegistry = new QuestNodeHandlerRegistry(Array.Empty<IQuestNodeHandler>());

        var node = new QuestNode
        {
            Id = Guid.NewGuid(),
            Name = "n",
            NodeType = QuestNodeType.BlockchainExecute,
            IsEntry = true,
            IsTerminal = true
        };
        var questId = Guid.NewGuid();
        var quest = new QuestEntity
        {
            Id = questId,
            AvatarId = Guid.NewGuid(),
            Nodes = new List<QuestNode> { node }
        };

        var store = new Mock<IQuestStore>();
        store.Setup(s => s.GetQuestAsync(questId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new OASISResult<QuestEntity> { Result = quest });
        store.Setup(s => s.UpsertQuestAsync(It.IsAny<QuestEntity>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((QuestEntity q, CancellationToken _) => new OASISResult<QuestEntity> { Result = q });

        var validator = new Mock<IQuestDagValidator>();
        var execStore = new InMemoryQuestNodeExecutionStore();
        var manager = new QuestManager(
            store.Object,
            new InMemoryQuestRunStore(),
            execStore,
            validator.Object,
            emptyRegistry);

        var result = await manager.ExecuteNodeAsync(questId, node.Id, quest.AvatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Be($"Unsupported node type: {QuestNodeType.BlockchainExecute}");
        result.Result.Should().NotBeNull();
        result.Result!.State.Should().Be(QuestNodeState.Failed);
        result.Result.Error.Should().Be($"Unsupported node type: {QuestNodeType.BlockchainExecute}");
    }
}
