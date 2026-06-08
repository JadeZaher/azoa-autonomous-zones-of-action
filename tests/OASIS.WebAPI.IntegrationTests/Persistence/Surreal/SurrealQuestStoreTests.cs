using FluentAssertions;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Connection;
using Oasis.SurrealDb.Client.Query;
using OASIS.WebAPI.IntegrationTests.Factories;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Providers.Stores.Surreal;

namespace OASIS.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// Store-layer integration tests for <see cref="SurrealQuestStore"/>.
///
/// Each test instance gets its own SurrealDB namespace (inherited from
/// <see cref="IntegrationTestBase"/>); the harness applies every .surql
/// schema file at <c>InitializeAsync</c> so the quest / quest_node /
/// quest_edge / quest_template / quest_node_template tables exist before
/// the first store call.
///
/// Skipping behaviour: when the SurrealDB test container is not running
/// the tests no-op via an early <see cref="IntegrationTestBase.SkipIfSurrealDbUnavailableAsync"/>
/// guard (the same idiom as <see cref="SurrealBridgeStoreTests"/>).
/// </summary>
public class SurrealQuestStoreTests : IntegrationTestBase
{
    public SurrealQuestStoreTests(OASISTestWebApplicationFactory factory) : base(factory) { }

    // ── Quest CRUD round-trip ────────────────────────────────────────────────

    [SkippableFact]
    public async Task UpsertQuest_GetQuest_RoundTripsHeadAndChildren()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var quest = NewQuest("FirstQuest");
        var nodeA = NewNode(quest.Id, name: "stepA", order: 0, isEntry: true);
        var nodeB = NewNode(quest.Id, name: "stepB", order: 1, isTerminal: true);
        quest.Nodes.Add(nodeA);
        quest.Nodes.Add(nodeB);
        quest.Edges.Add(NewEdge(quest.Id, nodeA.Id, nodeB.Id));
        quest.Metadata["color"] = "purple";

        var upserted = await store.UpsertQuestAsync(quest);
        upserted.IsError.Should().BeFalse(upserted.Message);
        upserted.Result!.Id.Should().Be(quest.Id);

        var loaded = await store.GetQuestAsync(quest.Id);
        loaded.IsError.Should().BeFalse(loaded.Message);
        loaded.Result.Should().NotBeNull();
        loaded.Result!.Id.Should().Be(quest.Id);
        loaded.Result.Name.Should().Be("FirstQuest");
        loaded.Result.Metadata.Should().ContainKey("color").WhoseValue.Should().Be("purple");
        loaded.Result.Nodes.Should().HaveCount(2);
        loaded.Result.Nodes.Select(n => n.Name).Should().ContainInOrder("stepA", "stepB");
        loaded.Result.Edges.Should().HaveCount(1);
        loaded.Result.Edges[0].SourceNodeId.Should().Be(nodeA.Id);
        loaded.Result.Edges[0].TargetNodeId.Should().Be(nodeB.Id);
    }

    [SkippableFact]
    public async Task GetQuest_NonExistent_ReturnsError()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var loaded = await store.GetQuestAsync(Guid.NewGuid());
        loaded.IsError.Should().BeTrue();
        loaded.Result.Should().BeNull();
    }

    [SkippableFact]
    public async Task UpsertQuest_RewritesChildren_OldNodesPurgedOnSecondUpsert()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var quest = NewQuest("ReshapeMe");
        var nodeA = NewNode(quest.Id, "A", 0, isEntry: true);
        var nodeB = NewNode(quest.Id, "B", 1, isTerminal: true);
        quest.Nodes.AddRange(new[] { nodeA, nodeB });
        quest.Edges.Add(NewEdge(quest.Id, nodeA.Id, nodeB.Id));
        (await store.UpsertQuestAsync(quest)).IsError.Should().BeFalse();

        // Second upsert replaces the graph entirely.
        var nodeC = NewNode(quest.Id, "C", 0, isEntry: true, isTerminal: true);
        quest.Nodes = new List<QuestNode> { nodeC };
        quest.Edges = new List<QuestEdge>();
        (await store.UpsertQuestAsync(quest)).IsError.Should().BeFalse();

        var loaded = await store.GetQuestAsync(quest.Id);
        loaded.IsError.Should().BeFalse();
        loaded.Result!.Nodes.Should().HaveCount(1, "stale nodes from the prior shape must be purged on re-upsert");
        loaded.Result.Nodes[0].Name.Should().Be("C");
        loaded.Result.Edges.Should().BeEmpty("stale edges from the prior shape must be purged on re-upsert");
    }

    [SkippableFact]
    public async Task GetQuestsByAvatar_FiltersByAvatar()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var avatarA = Guid.NewGuid();
        var avatarB = Guid.NewGuid();

        var q1 = NewQuest("AvatarAQuest1", avatarA);
        var q2 = NewQuest("AvatarAQuest2", avatarA);
        var q3 = NewQuest("AvatarBQuest1", avatarB);

        await store.UpsertQuestAsync(q1);
        await store.UpsertQuestAsync(q2);
        await store.UpsertQuestAsync(q3);

        var matches = await store.GetQuestsByAvatarAsync(avatarA);
        matches.IsError.Should().BeFalse();
        matches.Result!.Select(q => q.Id).Should().Contain(new[] { q1.Id, q2.Id });
        matches.Result!.Select(q => q.Id).Should().NotContain(q3.Id);
    }

    [SkippableFact]
    public async Task DeleteQuest_RemovesHeadAndChildren()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var quest = NewQuest("ToDelete");
        quest.Nodes.Add(NewNode(quest.Id, "Only", 0, isEntry: true, isTerminal: true));
        (await store.UpsertQuestAsync(quest)).IsError.Should().BeFalse();

        var deleteRes = await store.DeleteQuestAsync(quest.Id);
        deleteRes.IsError.Should().BeFalse();
        deleteRes.Result.Should().BeTrue();

        var fetched = await store.GetQuestAsync(quest.Id);
        fetched.IsError.Should().BeTrue("the head row is gone");
    }

    [SkippableFact]
    public async Task DeleteQuest_NonExistent_ReturnsErrorResultWithFalse()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var deleteRes = await store.DeleteQuestAsync(Guid.NewGuid());
        deleteRes.IsError.Should().BeTrue();
        deleteRes.Result.Should().BeFalse();
    }

    // ── QuestTemplate CRUD round-trip ────────────────────────────────────────

    [SkippableFact]
    public async Task UpsertQuestTemplate_GetQuestTemplate_RoundTrips()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var template = new QuestTemplate
        {
            Id              = Guid.NewGuid(),
            Name            = "OnboardTemplate",
            Description     = "Sample",
            AuthorAvatarId  = Guid.NewGuid(),
            Parameters      = "{\"foo\":\"bar\"}",
            Version         = "1.2.3",
            IsPublic        = true,
            Tags            = new List<string> { "alpha", "beta" },
        };

        var up = await store.UpsertQuestTemplateAsync(template);
        up.IsError.Should().BeFalse(up.Message);

        var loaded = await store.GetQuestTemplateAsync(template.Id);
        loaded.IsError.Should().BeFalse(loaded.Message);
        loaded.Result!.Id.Should().Be(template.Id);
        loaded.Result.Name.Should().Be("OnboardTemplate");
        loaded.Result.Parameters.Should().Be("{\"foo\":\"bar\"}");
        loaded.Result.Version.Should().Be("1.2.3");
        loaded.Result.IsPublic.Should().BeTrue();
        loaded.Result.Tags.Should().ContainInOrder("alpha", "beta");
    }

    // ── QuestNodeTemplate CRUD round-trip ────────────────────────────────────

    [SkippableFact]
    public async Task UpsertNodeTemplate_GetAll_IncludesIt()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var nt = new QuestNodeTemplate
        {
            Id              = Guid.NewGuid(),
            Name            = "HolonCreateTemplate",
            NodeType        = QuestNodeType.HolonCreate,
            DefaultConfig   = "{\"x\":1}",
            ConfigSchema    = "{}",
            InputSchema     = "{}",
            OutputSchema    = "{}",
            Version         = "1.0.0",
            AuthorAvatarId  = Guid.NewGuid(),
            IsPublic        = false,
            Tags            = new List<string> { "holon" },
        };

        var up = await store.UpsertQuestNodeTemplateAsync(nt);
        up.IsError.Should().BeFalse(up.Message);

        var all = await store.GetAllQuestNodeTemplatesAsync();
        all.IsError.Should().BeFalse();
        all.Result!.Select(x => x.Id).Should().Contain(nt.Id);
        var fetched = all.Result!.First(x => x.Id == nt.Id);
        fetched.NodeType.Should().Be(QuestNodeType.HolonCreate);
        fetched.DefaultConfig.Should().Be("{\"x\":1}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Quest NewQuest(string name, Guid? avatarId = null) => new()
    {
        Id           = Guid.NewGuid(),
        AvatarId     = avatarId ?? Guid.NewGuid(),
        Name         = name,
        Description  = "test quest",
        Metadata     = new Dictionary<string, string>(),
        CreatedDate  = DateTime.UtcNow,
    };

    private static QuestNode NewNode(Guid questId, string name, int order, bool isEntry = false, bool isTerminal = false) => new()
    {
        Id              = Guid.NewGuid(),
        QuestId         = questId,
        NodeType        = QuestNodeType.HolonGet,
        Name            = name,
        Config          = "{}",
        IsEntry         = isEntry,
        IsTerminal      = isTerminal,
        ExecutionOrder  = order,
    };

    private static QuestEdge NewEdge(Guid questId, Guid src, Guid tgt) => new()
    {
        Id            = Guid.NewGuid(),
        QuestId       = questId,
        SourceNodeId  = src,
        TargetNodeId  = tgt,
        EdgeType      = QuestEdgeType.Control,
    };

    private async Task<SurrealQuestStore> CreateStoreAsync()
    {
        var executor = await CreateExecutorAsync();
        return new SurrealQuestStore(executor);
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
        var http = new HttpClient();
        var connection = new HttpSurrealConnection(http, options);
        ISurrealExecutor executor = new DefaultSurrealExecutor(connection);
        return Task.FromResult(executor);
    }
}
