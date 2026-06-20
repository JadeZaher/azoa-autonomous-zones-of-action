using FluentAssertions;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Connection;
using Oasis.SurrealDb.Client.Query;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Providers.Stores.Surreal;

namespace OASIS.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// Store-layer integration tests for <see cref="SurrealQuestTemplateStore"/>.
///
/// The store is read-only; tests seed rows via raw CREATE statements through
/// the same <see cref="ISurrealExecutor"/> the store uses, then exercise the
/// public read API.
/// </summary>
public sealed class SurrealQuestTemplateStoreTests : IAsyncLifetime
{
    // Connection config sourced from SurrealTestDefaults (points at local instance).

    private readonly string _testNamespace = $"test{Guid.NewGuid():N}";
    private SurrealQuestTemplateStore _store = null!;
    private ISurrealExecutor _executor = null!;
    private HttpSurrealConnection _connection = null!;
    private bool _surrealAvailable;

    public async Task InitializeAsync()
    {
        _surrealAvailable = await ProbeSurrealAsync();
        if (!_surrealAvailable) return;

        var options = new SurrealConnectionOptions
        {
            Endpoint  = SurrealTestDefaults.Endpoint,
            Namespace = _testNamespace,
            Database  = "test",
            User      = SurrealTestDefaults.User,
            Password  = SurrealTestDefaults.Password,
        };

        var http = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        _connection = new HttpSurrealConnection(http, options);
        _executor = new DefaultSurrealExecutor(_connection);
        _store = new SurrealQuestTemplateStore(_executor);

        await BootstrapSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (!_surrealAvailable || _connection is null) return;
        try { await DropNamespaceAsync(); } catch { /* best-effort */ }
        finally { _connection.Dispose(); }
    }

    [SkippableFact]
    public async Task GetTemplate_UnknownId_ReturnsNull()
    {
        Skip.IfNot(_surrealAvailable, $"SurrealDB test container not available on {SurrealTestDefaults.Endpoint}");

        var result = await _store.GetTemplateAsync(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeNull();
    }

    [SkippableFact]
    public async Task GetNodeTemplate_UnknownId_ReturnsNull()
    {
        Skip.IfNot(_surrealAvailable, $"SurrealDB test container not available on {SurrealTestDefaults.Endpoint}");

        var result = await _store.GetNodeTemplateAsync(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeNull();
    }

    [SkippableFact]
    public async Task GetTemplate_ExistingTemplate_ReturnsWithEmbeddedNodesAndEdges()
    {
        Skip.IfNot(_surrealAvailable, $"SurrealDB test container not available on {SurrealTestDefaults.Endpoint}");

        var templateId = Guid.NewGuid();
        var nodeTemplateId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var slot1NodeId = Guid.NewGuid();
        var slot2NodeId = Guid.NewGuid();
        var edge1Id = Guid.NewGuid();

        await SeedTemplateAsync(
            templateId,
            authorId,
            name: "Simple Quest",
            description: "A simple quest template",
            parameters: "{\"required\":[\"holonId\"]}",
            tags: new[] { "tutorial", "starter" },
            nodes: new[]
            {
                CreateNodeSlot(slot1NodeId, "start", nodeTemplateId, isEntry: true, isTerminal: false),
                CreateNodeSlot(slot2NodeId, "end", nodeTemplateId, isEntry: false, isTerminal: true),
            },
            edges: new[]
            {
                CreateEdgeSlot(edge1Id, "start", "end", "Control"),
            });

        var template = await _store.GetTemplateAsync(templateId, CancellationToken.None);

        template.Should().NotBeNull();
        template!.Id.Should().Be(templateId);
        template.Name.Should().Be("Simple Quest");
        template.Description.Should().Be("A simple quest template");
        template.AuthorAvatarId.Should().Be(authorId);
        template.Parameters.Should().Be("{\"required\":[\"holonId\"]}");
        template.Tags.Should().BeEquivalentTo(new[] { "tutorial", "starter" });

        template.Nodes.Should().HaveCount(2);
        var entry = template.Nodes.Single(n => n.IsEntry);
        entry.SlotId.Should().Be("start");
        entry.NodeTemplateId.Should().Be(nodeTemplateId);
        entry.TemplateId.Should().Be(templateId);
        var terminal = template.Nodes.Single(n => n.IsTerminal);
        terminal.SlotId.Should().Be("end");

        template.Edges.Should().HaveCount(1);
        template.Edges[0].SourceSlotId.Should().Be("start");
        template.Edges[0].TargetSlotId.Should().Be("end");
        template.Edges[0].EdgeType.Should().Be(QuestEdgeType.Control);
        template.Edges[0].TemplateId.Should().Be(templateId);
    }

    [SkippableFact]
    public async Task GetNodeTemplate_ExistingRow_ReturnsParsedEnumAndDefaults()
    {
        Skip.IfNot(_surrealAvailable, $"SurrealDB test container not available on {SurrealTestDefaults.Endpoint}");

        var nodeTemplateId = Guid.NewGuid();
        var authorId = Guid.NewGuid();

        await SeedNodeTemplateAsync(
            nodeTemplateId,
            authorId,
            name: "Create Holon",
            nodeType: "HolonCreate",
            defaultConfig: "{\"holonType\":\"default\"}");

        var nt = await _store.GetNodeTemplateAsync(nodeTemplateId, CancellationToken.None);

        nt.Should().NotBeNull();
        nt!.Id.Should().Be(nodeTemplateId);
        nt.Name.Should().Be("Create Holon");
        nt.NodeType.Should().Be(QuestNodeType.HolonCreate);
        nt.DefaultConfig.Should().Be("{\"holonType\":\"default\"}");
        nt.AuthorAvatarId.Should().Be(authorId);
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private async Task SeedTemplateAsync(
        Guid templateId,
        Guid authorId,
        string name,
        string description,
        string parameters,
        string[] tags,
        object[] nodes,
        object[] edges)
    {
        var content = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = templateId.ToString("N").ToLowerInvariant(),
            ["name"] = name,
            ["description"] = description,
            ["author_avatar_id"] = authorId.ToString("N").ToLowerInvariant(),
            ["parameters"] = parameters,
            ["version"] = "1.0.0",
            ["is_public"] = false,
            ["nodes"] = nodes,
            ["edges"] = edges,
            ["tags"] = tags,
        };

        var q = SurrealQuery
            .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
            .WithParam("_t", "quest_template")
            .WithParam("_id", templateId.ToString("N").ToLowerInvariant())
            .WithParam("_body", content);

        var resp = await _executor.ExecuteAsync(q, CancellationToken.None);
        resp.EnsureAllOk();
    }

    private async Task SeedNodeTemplateAsync(
        Guid nodeTemplateId,
        Guid authorId,
        string name,
        string nodeType,
        string defaultConfig)
    {
        var content = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = nodeTemplateId.ToString("N").ToLowerInvariant(),
            ["name"] = name,
            ["node_type"] = nodeType,
            ["description"] = (string?)null,
            ["default_config"] = defaultConfig,
            ["config_schema"] = "{}",
            ["input_schema"] = "{}",
            ["output_schema"] = "{}",
            ["version"] = "1.0.0",
            ["author_avatar_id"] = authorId.ToString("N").ToLowerInvariant(),
            ["is_public"] = false,
            ["tags"] = Array.Empty<string>(),
        };

        var q = SurrealQuery
            .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
            .WithParam("_t", "quest_node_template")
            .WithParam("_id", nodeTemplateId.ToString("N").ToLowerInvariant())
            .WithParam("_body", content);

        var resp = await _executor.ExecuteAsync(q, CancellationToken.None);
        resp.EnsureAllOk();
    }

    private static Dictionary<string, object?> CreateNodeSlot(
        Guid id, string slotId, Guid nodeTemplateId, bool isEntry, bool isTerminal) =>
        new(StringComparer.Ordinal)
        {
            ["id"] = id.ToString("N").ToLowerInvariant(),
            ["slot_id"] = slotId,
            ["node_template_id"] = nodeTemplateId.ToString("N").ToLowerInvariant(),
            ["param_overrides"] = "{}",
            ["is_entry"] = isEntry,
            ["is_terminal"] = isTerminal,
        };

    private static Dictionary<string, object?> CreateEdgeSlot(
        Guid id, string sourceSlotId, string targetSlotId, string edgeType) =>
        new(StringComparer.Ordinal)
        {
            ["id"] = id.ToString("N").ToLowerInvariant(),
            ["source_slot_id"] = sourceSlotId,
            ["target_slot_id"] = targetSlotId,
            ["edge_type"] = edgeType,
        };

    // ── Infrastructure ────────────────────────────────────────────────────────

    private static async Task<bool> ProbeSurrealAsync()
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var r = await probe.GetAsync($"{SurrealTestDefaults.Endpoint}/health");
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task BootstrapSchemaAsync()
    {
        using var ddlClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));
        ddlClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        // SurrealDB 3.x requires "Surreal-NS"/"Surreal-DB" headers; scope the
        // DDL client to the per-test namespace so the table DDL lands there.
        ddlClient.DefaultRequestHeaders.Add("Surreal-NS", _testNamespace);
        ddlClient.DefaultRequestHeaders.Add("Surreal-DB", "test");

        // The namespace/database identifiers cannot be $params in DDL. Create
        // the namespace at ROOT, then the database scoped to the namespace with
        // the NS header ONLY (a Surreal-DB header naming a not-yet-existing db
        // makes the connection-level USE fail). The table DDL below then runs
        // with both NS+DB headers set.
        using (var nsClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) })
        {
            nsClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
            await nsClient.PostAsync("/sql", new StringContent(
                $"DEFINE NAMESPACE IF NOT EXISTS {_testNamespace}", System.Text.Encoding.UTF8, "text/plain"));
            nsClient.DefaultRequestHeaders.Add("Surreal-NS", _testNamespace);
            await nsClient.PostAsync("/sql", new StringContent(
                "DEFINE DATABASE IF NOT EXISTS test", System.Text.Encoding.UTF8, "text/plain"));
        }

        const string ddl = """

            DEFINE TABLE IF NOT EXISTS quest_template SCHEMAFULL;
            DEFINE FIELD IF NOT EXISTS id               ON quest_template TYPE string ASSERT $value != NONE AND $value != "";
            DEFINE FIELD IF NOT EXISTS name             ON quest_template TYPE string;
            DEFINE FIELD IF NOT EXISTS description      ON quest_template TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS author_avatar_id ON quest_template TYPE string ASSERT $value != NONE AND $value != "";
            DEFINE FIELD IF NOT EXISTS parameters       ON quest_template TYPE string;
            DEFINE FIELD IF NOT EXISTS version          ON quest_template TYPE string;
            DEFINE FIELD IF NOT EXISTS is_public        ON quest_template TYPE bool DEFAULT false;
            DEFINE FIELD IF NOT EXISTS nodes            ON quest_template TYPE array;
            DEFINE FIELD IF NOT EXISTS edges            ON quest_template TYPE array;
            DEFINE FIELD IF NOT EXISTS tags             ON quest_template TYPE array;
            DEFINE INDEX IF NOT EXISTS quest_template_by_author ON quest_template FIELDS author_avatar_id;

            DEFINE TABLE IF NOT EXISTS quest_node_template SCHEMAFULL;
            DEFINE FIELD IF NOT EXISTS id               ON quest_node_template TYPE string ASSERT $value != NONE AND $value != "";
            DEFINE FIELD IF NOT EXISTS name             ON quest_node_template TYPE string;
            DEFINE FIELD IF NOT EXISTS node_type        ON quest_node_template TYPE string;
            DEFINE FIELD IF NOT EXISTS description      ON quest_node_template TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS default_config   ON quest_node_template TYPE string;
            DEFINE FIELD IF NOT EXISTS config_schema    ON quest_node_template TYPE string;
            DEFINE FIELD IF NOT EXISTS input_schema     ON quest_node_template TYPE string;
            DEFINE FIELD IF NOT EXISTS output_schema    ON quest_node_template TYPE string;
            DEFINE FIELD IF NOT EXISTS version          ON quest_node_template TYPE string;
            DEFINE FIELD IF NOT EXISTS author_avatar_id ON quest_node_template TYPE string ASSERT $value != NONE AND $value != "";
            DEFINE FIELD IF NOT EXISTS is_public        ON quest_node_template TYPE bool DEFAULT false;
            DEFINE FIELD IF NOT EXISTS tags             ON quest_node_template TYPE array;
            DEFINE INDEX IF NOT EXISTS quest_node_template_by_author ON quest_node_template FIELDS author_avatar_id;
            DEFINE INDEX IF NOT EXISTS quest_node_template_by_type   ON quest_node_template FIELDS node_type
            """;

        var content = new StringContent(ddl, System.Text.Encoding.UTF8, "text/plain");
        var response = await ddlClient.PostAsync("/sql", content);
        _ = response;
    }

    private async Task DropNamespaceAsync()
    {
        using var dropClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));
        dropClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
                var removeSql = $"REMOVE NAMESPACE IF EXISTS {_testNamespace}";
        var content = new StringContent(removeSql, System.Text.Encoding.UTF8, "text/plain");
        await dropClient.PostAsync("/sql", content);
    }
}
