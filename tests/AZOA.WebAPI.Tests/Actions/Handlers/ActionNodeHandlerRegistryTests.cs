using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions.Handlers;

public class ActionNodeHandlerRegistryTests
{
    private static IActionNodeHandler Handler(ActionNodeType type)
    {
        var m = new Mock<IActionNodeHandler>();
        m.SetupGet(h => h.NodeType).Returns(type);
        return m.Object;
    }

    [Fact]
    public void Registry_ResolvesRegisteredHandlerByType()
    {
        var holonGet = Handler(ActionNodeType.HolonGet);
        var registry = new ActionNodeHandlerRegistry(new[] { holonGet, Handler(ActionNodeType.NftMint) });

        registry.TryGet(ActionNodeType.HolonGet, out var resolved).Should().BeTrue();
        resolved.Should().BeSameAs(holonGet);
    }

    [Fact]
    public void Registry_UnknownType_TryGetReturnsFalse()
    {
        var registry = new ActionNodeHandlerRegistry(new[] { Handler(ActionNodeType.HolonGet) });

        registry.TryGet(ActionNodeType.ComposeOutputs, out var resolved).Should().BeFalse();
        resolved.Should().BeNull();
    }

    [Fact]
    public void Registry_DuplicateNodeType_Throws()
    {
        var act = () => new ActionNodeHandlerRegistry(new[]
        {
            Handler(ActionNodeType.HolonGet),
            Handler(ActionNodeType.HolonGet)
        });

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Duplicate action node handler for HolonGet*");
    }

    [Fact]
    public async Task ActionManager_UnknownNodeType_RegistryMiss_FailsExecutionRow()
    {
        // Registry with no handler for the node's type => TryGet false =>
        // ActionManager records a Failed ActionNodeExecution with the
        // "Unsupported node type" error message (preserves the former
        // default-case Fail behaviour, now keyed by (runId, nodeId)).
        var emptyRegistry = new ActionNodeHandlerRegistry(Array.Empty<IActionNodeHandler>());

        var node = new ActionNode
        {
            Id = Guid.NewGuid(),
            Name = "n",
            NodeType = ActionNodeType.BlockchainExecute,
            IsEntry = true,
            IsTerminal = true
        };
        var actionId = Guid.NewGuid();
        var action = new ActionEntity
        {
            Id = actionId,
            UserId = Guid.NewGuid(),
            Nodes = new List<ActionNode> { node }
        };

        var store = new Mock<IActionStore>();
        store.Setup(s => s.GetActionAsync(actionId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new AZOAResult<ActionEntity> { Result = action });
        store.Setup(s => s.UpsertActionAsync(It.IsAny<ActionEntity>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((ActionEntity q, CancellationToken _) => new AZOAResult<ActionEntity> { Result = q });

        var validator = new Mock<IActionDagValidator>();
        var execStore = new InMemoryActionNodeExecutionStore();
        var manager = new ActionManager(
            store.Object,
            new InMemoryActionRunStore(),
            execStore,
            validator.Object,
            new Mock<IActionDagExecutabilityValidator>().Object,
            emptyRegistry,
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var result = await manager.ExecuteNodeAsync(actionId, node.Id, action.UserId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Be($"Unsupported node type: {ActionNodeType.BlockchainExecute}");
        result.Result.Should().NotBeNull();
        result.Result!.State.Should().Be(ActionNodeState.Failed);
        result.Result.Error.Should().Be($"Unsupported node type: {ActionNodeType.BlockchainExecute}");
    }
}
