using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Providers.Stores.Surreal;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// Store-layer integration tests for <see cref="SurrealActionStore"/>.
///
/// Each test instance gets its own SurrealDB namespace (inherited from
/// <see cref="IntegrationTestBase"/>); the harness applies every .surql
/// schema file at <c>InitializeAsync</c> so the action / action_node /
/// action_edge / action_template / action_node_template tables exist before
/// the first store call.
///
/// Skipping behaviour: when the SurrealDB test container is not running
/// the tests no-op via an early <see cref="IntegrationTestBase.SkipIfSurrealDbUnavailableAsync"/>
/// guard (the same idiom as <see cref="SurrealBridgeStoreTests"/>).
/// </summary>
public class SurrealActionStoreTests : IntegrationTestBase
{
    public SurrealActionStoreTests(AZOATestWebApplicationFactory factory) : base(factory) { }

    // ── Action CRUD round-trip ────────────────────────────────────────────────

    [SkippableFact]
    public async Task UpsertAction_GetAction_RoundTripsHeadAndChildren()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var action = NewAction("FirstAction");
        var nodeA = NewNode(action.Id, name: "stepA", order: 0, isEntry: true);
        var nodeB = NewNode(action.Id, name: "stepB", order: 1, isTerminal: true);
        action.Nodes.Add(nodeA);
        action.Nodes.Add(nodeB);
        action.Edges.Add(NewEdge(action.Id, nodeA.Id, nodeB.Id));
        action.Metadata["color"] = "purple";

        var upserted = await store.UpsertActionAsync(action);
        upserted.IsError.Should().BeFalse(upserted.Message);
        upserted.Result!.Id.Should().Be(action.Id);

        var loaded = await store.GetActionAsync(action.Id);
        loaded.IsError.Should().BeFalse(loaded.Message);
        loaded.Result.Should().NotBeNull();
        loaded.Result!.Id.Should().Be(action.Id);
        loaded.Result.Name.Should().Be("FirstAction");
        loaded.Result.Metadata.Should().ContainKey("color").WhoseValue.Should().Be("purple");
        loaded.Result.Nodes.Should().HaveCount(2);
        loaded.Result.Nodes.Select(n => n.Name).Should().ContainInOrder("stepA", "stepB");
        loaded.Result.Edges.Should().HaveCount(1);
        loaded.Result.Edges[0].SourceNodeId.Should().Be(nodeA.Id);
        loaded.Result.Edges[0].TargetNodeId.Should().Be(nodeB.Id);
    }

    [SkippableFact]
    public async Task GetAction_NonExistent_ReturnsError()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var loaded = await store.GetActionAsync(Guid.NewGuid());
        loaded.IsError.Should().BeTrue();
        loaded.Result.Should().BeNull();
    }

    [SkippableFact]
    public async Task UpsertAction_RewritesChildren_OldNodesPurgedOnSecondUpsert()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var action = NewAction("ReshapeMe");
        var nodeA = NewNode(action.Id, "A", 0, isEntry: true);
        var nodeB = NewNode(action.Id, "B", 1, isTerminal: true);
        action.Nodes.AddRange(new[] { nodeA, nodeB });
        action.Edges.Add(NewEdge(action.Id, nodeA.Id, nodeB.Id));
        (await store.UpsertActionAsync(action)).IsError.Should().BeFalse();

        // Second upsert replaces the graph entirely.
        var nodeC = NewNode(action.Id, "C", 0, isEntry: true, isTerminal: true);
        action.Nodes = new List<ActionNode> { nodeC };
        action.Edges = new List<ActionEdge>();
        (await store.UpsertActionAsync(action)).IsError.Should().BeFalse();

        var loaded = await store.GetActionAsync(action.Id);
        loaded.IsError.Should().BeFalse();
        loaded.Result!.Nodes.Should().HaveCount(1, "stale nodes from the prior shape must be purged on re-upsert");
        loaded.Result.Nodes[0].Name.Should().Be("C");
        loaded.Result.Edges.Should().BeEmpty("stale edges from the prior shape must be purged on re-upsert");
    }

    [SkippableFact]
    public async Task GetActionsByUser_FiltersByUser()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var q1 = NewAction("UserAAction1", userA);
        var q2 = NewAction("UserAAction2", userA);
        var q3 = NewAction("UserBAction1", userB);

        await store.UpsertActionAsync(q1);
        await store.UpsertActionAsync(q2);
        await store.UpsertActionAsync(q3);

        var matches = await store.GetActionsByUserAsync(userA);
        matches.IsError.Should().BeFalse();
        matches.Result!.Select(q => q.Id).Should().Contain(new[] { q1.Id, q2.Id });
        matches.Result!.Select(q => q.Id).Should().NotContain(q3.Id);
    }

    [SkippableFact]
    public async Task DeleteAction_RemovesHeadAndChildren()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var action = NewAction("ToDelete");
        action.Nodes.Add(NewNode(action.Id, "Only", 0, isEntry: true, isTerminal: true));
        (await store.UpsertActionAsync(action)).IsError.Should().BeFalse();

        var deleteRes = await store.DeleteActionAsync(action.Id);
        deleteRes.IsError.Should().BeFalse();
        deleteRes.Result.Should().BeTrue();

        var fetched = await store.GetActionAsync(action.Id);
        fetched.IsError.Should().BeTrue("the head row is gone");
    }

    [SkippableFact]
    public async Task DeleteAction_NonExistent_ReturnsErrorResultWithFalse()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var deleteRes = await store.DeleteActionAsync(Guid.NewGuid());
        deleteRes.IsError.Should().BeTrue();
        deleteRes.Result.Should().BeFalse();
    }

    // ── ActionTemplate CRUD round-trip ────────────────────────────────────────

    [SkippableFact]
    public async Task UpsertActionTemplate_GetActionTemplate_RoundTrips()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var template = new ActionTemplate
        {
            Id              = Guid.NewGuid(),
            Name            = "OnboardTemplate",
            Description     = "Sample",
            AuthorUserId  = Guid.NewGuid(),
            Parameters      = "{\"foo\":\"bar\"}",
            Version         = "1.2.3",
            IsPublic        = true,
            Tags            = new List<string> { "alpha", "beta" },
        };

        var up = await store.UpsertActionTemplateAsync(template);
        up.IsError.Should().BeFalse(up.Message);

        var loaded = await store.GetActionTemplateAsync(template.Id);
        loaded.IsError.Should().BeFalse(loaded.Message);
        loaded.Result!.Id.Should().Be(template.Id);
        loaded.Result.Name.Should().Be("OnboardTemplate");
        loaded.Result.Parameters.Should().Be("{\"foo\":\"bar\"}");
        loaded.Result.Version.Should().Be("1.2.3");
        loaded.Result.IsPublic.Should().BeTrue();
        loaded.Result.Tags.Should().ContainInOrder("alpha", "beta");
    }

    // ── ActionNodeTemplate CRUD round-trip ────────────────────────────────────

    [SkippableFact]
    public async Task UpsertNodeTemplate_GetAll_IncludesIt()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var nt = new ActionNodeTemplate
        {
            Id              = Guid.NewGuid(),
            Name            = "HolonCreateTemplate",
            NodeType        = ActionNodeType.HolonCreate,
            DefaultConfig   = "{\"x\":1}",
            ConfigSchema    = "{}",
            InputSchema     = "{}",
            OutputSchema    = "{}",
            Version         = "1.0.0",
            AuthorUserId  = Guid.NewGuid(),
            IsPublic        = false,
            Tags            = new List<string> { "holon" },
        };

        var up = await store.UpsertActionNodeTemplateAsync(nt);
        up.IsError.Should().BeFalse(up.Message);

        var all = await store.GetAllActionNodeTemplatesAsync();
        all.IsError.Should().BeFalse();
        all.Result!.Select(x => x.Id).Should().Contain(nt.Id);
        var fetched = all.Result!.First(x => x.Id == nt.Id);
        fetched.NodeType.Should().Be(ActionNodeType.HolonCreate);
        fetched.DefaultConfig.Should().Be("{\"x\":1}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ActionDefinition NewAction(string name, Guid? userId = null) => new()
    {
        Id           = Guid.NewGuid(),
        UserId     = userId ?? Guid.NewGuid(),
        Name         = name,
        Description  = "test action",
        Metadata     = new Dictionary<string, string>(),
        CreatedDate  = DateTime.UtcNow,
    };

    private static ActionNode NewNode(Guid actionId, string name, int order, bool isEntry = false, bool isTerminal = false) => new()
    {
        Id              = Guid.NewGuid(),
        ActionId         = actionId,
        NodeType        = ActionNodeType.HolonGet,
        Name            = name,
        Config          = "{}",
        IsEntry         = isEntry,
        IsTerminal      = isTerminal,
        ExecutionOrder  = order,
    };

    private static ActionEdge NewEdge(Guid actionId, Guid src, Guid tgt) => new()
    {
        Id            = Guid.NewGuid(),
        ActionId       = actionId,
        SourceNodeId  = src,
        TargetNodeId  = tgt,
        EdgeType      = ActionEdgeType.Control,
    };

    private async Task<SurrealActionStore> CreateStoreAsync()
    {
        var executor = await CreateExecutorAsync();
        return new SurrealActionStore(executor);
    }

    private Task<ISurrealExecutor> CreateExecutorAsync()
    {
        var options = new SurrealConnectionOptions
        {
            Endpoint   = SurrealTestDefaults.Endpoint,
            Namespace  = TestNamespace,
            Database   = "test",
            User       = SurrealTestDefaults.User,
            Password   = SurrealTestDefaults.Password,
        };
        var connection = new SurrealDbNetConnection(options);
        ISurrealExecutor executor = new DefaultSurrealExecutor(connection);
        return Task.FromResult(executor);
    }
}
