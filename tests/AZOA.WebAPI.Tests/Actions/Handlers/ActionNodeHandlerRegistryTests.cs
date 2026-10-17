using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Tests.Fakes;
using AZOA.WebAPI.Tests.TestSupport;
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
    public void Registry_ResolvesRegisteredHandlerByType_AndWrapsReadNodesForRecovery()
    {
        var nftMint = Handler(ActionNodeType.NftMint);
        var registry = new ActionNodeHandlerRegistry(new[] { Handler(ActionNodeType.HolonGet), nftMint });

        registry.TryGet(ActionNodeType.NftMint, out var resolved).Should().BeTrue();
        resolved.Should().BeSameAs(nftMint);
        registry.TryGet(ActionNodeType.HolonGet, out var read).Should().BeTrue();
        read.Should().BeOfType<ActionReadHandler>("read nodes run behind the read-recovery decorator");
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
    public async Task Durable_step_with_no_registered_handler_fails_the_execution_row()
    {
        // A registry miss is refused at the durable step: the node's execution row records
        // the "Unsupported node type" failure instead of dispatching anything.
        var node = new ActionNode
        {
            Id = Guid.NewGuid(),
            Name = "n",
            NodeType = ActionNodeType.BlockchainExecute,
            IsEntry = true,
            IsTerminal = true
        };
        var action = new ActionEntity
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Status = ActionStatus.Active,
            PublishedVersionHash = "hash-v1",
            Nodes = new List<ActionNode> { node }
        };
        var store = new InMemoryActionStore();
        await store.UpsertActionAsync(action);
        var execStore = new InMemoryActionNodeExecutionStore();
        var harness = new DurableActionExecutionHarness(
            store, new InMemoryActionRunStore(), execStore,
            new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(Array.Empty<IActionNodeHandler>()),
            new InMemorySagaStore(), WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(), BindingResolverFakes.PassThrough());

        var run = await harness.ExecuteAsync(action.Id, action.UserId);

        run.IsError.Should().BeFalse(run.Message);
        var execution = (await execStore.GetByRunAndNodeAsync(run.Result!.Id, node.Id)).Result!;
        execution.State.Should().Be(ActionNodeState.Failed);
        execution.Error.Should().Be($"Unsupported node type: {ActionNodeType.BlockchainExecute}");
    }

    [Fact]
    public void Bridge_node_with_a_wtt_config_dispatches_to_the_recoverable_wtt_route_and_others_keep_their_handler()
    {
        var legacy = Handler(ActionNodeType.Bridge);
        var route = new Mock<IActionWttRoute>();
        route.Setup(r => r.Applies(It.Is<ActionNode>(n => n.Config.Contains("\"wtt\"")))).Returns(true);
        route.SetupGet(r => r.SigningScopes).Returns(["wtt:source:sign", "wtt:target:sign"]);
        var registry = new ActionNodeHandlerRegistry(new[] { legacy }, wttRoutes: [route.Object]);

        registry.TryGetFor(new ActionNode { NodeType = ActionNodeType.Bridge, Config = "{\"wtt\":{}}" }, out var wtt).Should().BeTrue();
        wtt.Should().BeAssignableTo<IRecoverableActionNodeHandler>();
        wtt.ResolveSigningScopes(new ActionNode()).Should().Equal("wtt:source:sign", "wtt:target:sign");
        registry.TryGetFor(new ActionNode { NodeType = ActionNodeType.Bridge, Config = "{\"sourceChain\":\"Algorand\"}" }, out var plain).Should().BeTrue();
        plain.Should().BeSameAs(legacy, "a legacy Bridge node never gains an external-operation reference");
    }
}
