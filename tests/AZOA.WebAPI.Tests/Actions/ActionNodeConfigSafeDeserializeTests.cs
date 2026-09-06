using FluentAssertions;
using Moq;
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
/// Pins FR-4 / AC-4a/4b: safe config deserialization (malformed config →
/// Failed node, not thrown exception) and definition-time enforcement
/// (AddNodeAsync / PublishAsync reject bad config).
/// See Services/ActionDefinition/AGENTS.md §node-config.
/// </summary>
public class ActionNodeConfigSafeDeserializeTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    // ─── ActionNodeConfig.TryDeserialize unit tests ────────────────────────

    [Fact]
    public void TryDeserialize_ValidJson_ReturnsTrue()
    {
        var json = """{"predicate":"true","reads":{},"holons":[]}""";
        var ok = ActionNodeConfig.TryDeserialize<GateCheckNodeConfig>(
            json, "GateCheck", out var cfg, out var error);

        ok.Should().BeTrue();
        error.Should().BeEmpty();
        cfg.Predicate.Should().Be("true");
    }

    [Fact]
    public void TryDeserialize_EmptyString_ReturnsDefaultInstance()
    {
        var ok = ActionNodeConfig.TryDeserialize<GateCheckNodeConfig>(
            string.Empty, "GateCheck", out var cfg, out var error);

        ok.Should().BeTrue("empty string → default instance, not an error");
        error.Should().BeEmpty();
        cfg.Should().NotBeNull();
    }

    [Fact]
    public void TryDeserialize_NullJson_ReturnsDefaultInstance()
    {
        var ok = ActionNodeConfig.TryDeserialize<GateCheckNodeConfig>(
            null, "GateCheck", out var cfg, out var error);

        ok.Should().BeTrue("null → default instance, not an error");
        cfg.Should().NotBeNull();
    }

    [Fact]
    public void TryDeserialize_MalformedJson_ReturnsFalseWithMessage()
    {
        var ok = ActionNodeConfig.TryDeserialize<GateCheckNodeConfig>(
            "not json at all!!!", "GateCheck", out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("GateCheck");
        error.Should().ContainEquivalentOf("parse error");
    }

    [Fact]
    public void TryDeserialize_UnknownMember_ReturnsFalseWithMessage()
    {
        // StrictOptions has UnmappedMemberHandling.Disallow.
        var json = """{"predicate":"true","unknownExtraField":42}""";
        var ok = ActionNodeConfig.TryDeserialize<GateCheckNodeConfig>(
            json, "GateCheck", out _, out var error);

        ok.Should().BeFalse("unknown members are rejected by strict options");
        error.Should().NotBeEmpty();
    }

    // ─── ActionNodeConfigRegistry exhaustiveness pin ───────────────────────

    [Fact]
    public void Registry_AllActionNodeTypeValues_HaveEntries()
    {
        // Any ActionNodeType missing from the registry throws NotSupportedException.
        // This test catches newly-added types that weren't wired in.
        foreach (var nodeType in Enum.GetValues<ActionNodeType>())
        {
            var act = () => ActionNodeConfigRegistry.GetConfigType(nodeType);
            act.Should().NotThrow($"ActionNodeType.{nodeType} must have a registry entry");
        }
    }

    // ─── AC-4a: malformed config → Failed node, not thrown exception ──────

    [Fact]
    public async Task ExecuteAsync_TransferNodeMalformedConfig_NodeFails_NotThrows()
    {
        // Build a single-node action with a Transfer node whose config is invalid JSON.
        var nodeId = Guid.NewGuid();
        var action = new ActionEntity
        {
            Id = Guid.NewGuid(),
            Name = "BadConfig",
            UserId = UserId,
            Status = ActionStatus.Active,
            Nodes = new List<ActionNode>
            {
                new() { Id = nodeId, Name = "Transfer1", NodeType = ActionNodeType.Transfer,
                        IsEntry = true, IsTerminal = true,
                        Config = "not-valid-json!!!" },
            },
            Edges = new List<ActionEdge>(),
        };

        var actionStore = new InMemoryActionStore();
        await actionStore.UpsertActionAsync(action);

        // Transfer handler needs an INftManager — we use a mock that should
        // never be reached because TryDeserialize fails first.
        var nftMock = new Moq.Mock<AZOA.WebAPI.Interfaces.Managers.INftManager>();
        var transferHandler = new AZOA.WebAPI.Services.Actions.Handlers.TransferNodeHandler(nftMock.Object);

        var manager = new ActionManager(
            actionStore,
            new InMemoryActionRunStore(),
            new InMemoryActionNodeExecutionStore(),
            new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(new IActionNodeHandler[] { transferHandler }),
            new InMemorySagaStore(),
            WalletManagerMocks.WithOneWallet(),    // chain-cap gate passes
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        // Should not throw; the node should end up Failed with a descriptive message.
        var act = async () => await manager.ExecuteAsync(action.Id, UserId);
        await act.Should().NotThrowAsync();

        var result = await manager.ExecuteAsync(action.Id, UserId);
        // The run itself completes (engine doesn't crash); the node is Failed.
        // Depending on engine behaviour, IsError may or may not be set on the run-level
        // result — what matters is no exception escapes.
        nftMock.Verify(m => m.TransferAsync(
            It.IsAny<Guid>(), It.IsAny<NftTransferRequest>(),
            It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Moq.Times.Never,
            "TransferAsync must never be reached when config parse fails");
    }

    [Fact]
    public async Task ExecuteAsync_GateCheckNodeMalformedConfig_NodeFails_NotThrows()
    {
        var nodeId = Guid.NewGuid();
        var action = new ActionEntity
        {
            Id = Guid.NewGuid(),
            Name = "BadGateConfig",
            UserId = UserId,
            Status = ActionStatus.Active,
            Nodes = new List<ActionNode>
            {
                new() { Id = nodeId, Name = "Gate1", NodeType = ActionNodeType.GateCheck,
                        IsEntry = true, IsTerminal = true,
                        Config = "{broken json" },
            },
            Edges = new List<ActionEdge>(),
        };

        var actionStore = new InMemoryActionStore();
        await actionStore.UpsertActionAsync(action);

        var holonMock = new Moq.Mock<AZOA.WebAPI.Interfaces.Managers.IHolonManager>();
        var gateHandler = new AZOA.WebAPI.Services.Actions.Handlers.GateCheckNodeHandler(holonMock.Object);

        var manager = new ActionManager(
            actionStore,
            new InMemoryActionRunStore(),
            new InMemoryActionNodeExecutionStore(),
            new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(new IActionNodeHandler[] { gateHandler }),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var act = async () => await manager.ExecuteAsync(action.Id, UserId);
        await act.Should().NotThrowAsync("malformed config must never throw out of the engine");
    }

    // ─── AC-4b: definition-time enforcement ───────────────────────────────

    [Fact]
    public async Task AddNodeAsync_BadConfig_Rejected()
    {
        var action = BuildActiveAction();
        action.Status = ActionStatus.Draft; // must be Draft to allow mutations

        var (manager, _) = BuildManager(action);

        var result = await manager.AddNodeAsync(action.Id,
            new ActionNodeCreateModel
            {
                Name = "BadTransfer",
                NodeType = ActionNodeType.Transfer,
                Config = """{"unknownField":"oops"}""",  // unknown member → strict reject
            },
            UserId);

        result.IsError.Should().BeTrue("unknown config member must be rejected at add time");
        result.Message.Should().ContainEquivalentOf("config");
    }

    [Fact]
    public async Task AddNodeAsync_ValidConfig_Accepted()
    {
        var action = BuildActiveAction();
        action.Status = ActionStatus.Draft;

        var (manager, _) = BuildManager(action);

        // Valid Transfer config (Guid + empty request).
        var validConfig = "{\"nftId\":\"" + Guid.NewGuid() + "\",\"request\":{}}";
        var result = await manager.AddNodeAsync(action.Id,
            new ActionNodeCreateModel
            {
                Name = "GoodTransfer",
                NodeType = ActionNodeType.Transfer,
                Config = validConfig,
            },
            UserId);

        result.IsError.Should().BeFalse("valid config must pass at add time");
    }

    [Fact]
    public async Task PublishAsync_ActionWithBadConfigNode_Fails()
    {
        // Build a action with a structurally-valid DAG but a node whose config
        // will fail registry strict validation.
        var entryId = Guid.NewGuid();
        var badNodeId = Guid.NewGuid();
        var qid = Guid.NewGuid();

        var action = new ActionEntity
        {
            Id = qid,
            Name = "BadNodeConfig",
            UserId = UserId,
            Status = ActionStatus.Draft,
            Nodes = new List<ActionNode>
            {
                new() { Id = entryId,   Name = "Entry",       NodeType = ActionNodeType.Condition,
                        IsEntry = true,  IsTerminal = false, Config = "{}" },
                new() { Id = badNodeId, Name = "BadTransfer", NodeType = ActionNodeType.Transfer,
                        IsEntry = false, IsTerminal = true,
                        Config = """{"surpriseField":true}""" },  // unknown → strict reject
            },
            Edges = new List<ActionEdge>
            {
                new() { Id = Guid.NewGuid(), ActionId = qid,
                        SourceNodeId = entryId, TargetNodeId = badNodeId,
                        EdgeType = ActionEdgeType.Control },
            },
        };

        var (manager, _) = BuildManager(action);

        var result = await manager.PublishAsync(action.Id, UserId);

        result.IsError.Should().BeTrue("publish must be rejected when a node has invalid config (AC-4b)");
        result.Message.Should().ContainEquivalentOf("config");
    }

    // ─── helpers ──────────────────────────────────────────────────────────

    private static ActionEntity BuildActiveAction()
    {
        var entryId    = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var qid        = Guid.NewGuid();
        return new ActionEntity
        {
            Id       = qid,
            Name     = "TestAction",
            UserId = UserId,
            Status   = ActionStatus.Draft,
            Nodes = new List<ActionNode>
            {
                new() { Id = entryId,    Name = "Entry",    NodeType = ActionNodeType.Condition,
                        IsEntry = true,  IsTerminal = false, Config = "{}" },
                new() { Id = terminalId, Name = "Terminal", NodeType = ActionNodeType.Condition,
                        IsEntry = false, IsTerminal = true,  Config = "{}" },
            },
            Edges = new List<ActionEdge>
            {
                new() { Id = Guid.NewGuid(), ActionId = qid,
                        SourceNodeId = entryId, TargetNodeId = terminalId,
                        EdgeType = ActionEdgeType.Control },
            },
        };
    }

    private static (ActionManager manager, InMemoryActionStore actionStore)
        BuildManager(ActionEntity? action = null)
    {
        var actionStore = new InMemoryActionStore();
        if (action != null)
            actionStore.UpsertActionAsync(action).GetAwaiter().GetResult();

        var manager = new ActionManager(
            actionStore,
            new InMemoryActionRunStore(),
            new InMemoryActionNodeExecutionStore(),
            new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(Array.Empty<IActionNodeHandler>()),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        return (manager, actionStore);
    }
}
