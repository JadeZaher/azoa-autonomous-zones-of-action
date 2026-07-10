using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IQuestTemplateStore"/>.
///
/// Pattern: mirrors <see cref="SurrealApiKeyStore"/> — Guid("N") lowercase-hex
/// record ids and inline POCO until the source-generator catches up to tables
/// 130/140. Replace the inline POCOs with the generated types when those
/// arrive.
///
/// Storage shape: template Nodes + Edges embed as FLEXIBLE array<object>
/// fields on the parent quest_template row so a template fetch is a single
/// record-id SELECT (no join). Sub-shapes (slot id, node-template id, edge
/// source/target) are validated by the C# POCO at deserialisation; the
/// SurrealDB FLEXIBLE field accepts arbitrary objects so the schema does not
/// need to be amended when the slot record grows new fields.
/// </summary>
public sealed class SurrealQuestTemplateStore : IQuestTemplateStore
{
    private const string TemplateTable = "quest_template";
    private const string NodeTemplateTable = "quest_node_template";

    private readonly ISurrealExecutor _executor;

    public SurrealQuestTemplateStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<QuestTemplate?> GetTemplateAsync(Guid templateId, CancellationToken ct)
    {
        var surrealId = SurrealId.ToSurrealId(templateId);

        var q = SurrealQuery
            .Of("SELECT * FROM type::record($_t, $_id)")
            .WithParam("_t", TemplateTable)
            .WithParam("_id", surrealId);

        var rows = await _executor.QueryAsync<QuestTemplatePoco>(q, ct);
        return rows.Count == 0 ? null : ToDomain(rows[0]);
    }

    public async Task<QuestNodeTemplate?> GetNodeTemplateAsync(Guid nodeTemplateId, CancellationToken ct)
    {
        var surrealId = SurrealId.ToSurrealId(nodeTemplateId);

        var q = SurrealQuery
            .Of("SELECT * FROM type::record($_t, $_id)")
            .WithParam("_t", NodeTemplateTable)
            .WithParam("_id", surrealId);

        var rows = await _executor.QueryAsync<QuestNodeTemplatePoco>(q, ct);
        return rows.Count == 0 ? null : ToDomain(rows[0]);
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────



    private static Guid FromSurrealIdStripped(string id)
    {
        var stripped = StripIdPrefix(id);
        return Guid.ParseExact(stripped, "N");
    }

    private static string StripIdPrefix(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var colon = raw.IndexOf(':');
        return colon >= 0 && colon < raw.Length - 1 ? raw[(colon + 1)..] : raw;
    }

    private static QuestTemplate ToDomain(QuestTemplatePoco p)
    {
        var template = new QuestTemplate
        {
            // SELECT * returns the record id in full `table:<hex>` form; strip the
            // table prefix before parsing the bare 32-hex Guid.
            Id             = FromSurrealIdStripped(p.Id),
            Name           = p.Name ?? string.Empty,
            Description    = p.Description,
            AuthorAvatarId = string.IsNullOrEmpty(p.AuthorAvatarId)
                             ? Guid.Empty
                             : FromSurrealIdStripped(p.AuthorAvatarId),
            Parameters     = string.IsNullOrEmpty(p.Parameters) ? "{}" : p.Parameters,
            Version        = string.IsNullOrEmpty(p.Version) ? "1.0.0" : p.Version,
            IsPublic       = p.IsPublic,
            Tags           = p.Tags?.ToList() ?? new List<string>(),
        };

        if (p.Nodes is not null)
        {
            foreach (var nodePoco in p.Nodes)
            {
                template.Nodes.Add(new QuestTemplateNode
                {
                    Id             = string.IsNullOrEmpty(nodePoco.Id)
                                     ? Guid.NewGuid()
                                     : SurrealId.FromSurrealId(nodePoco.Id),
                    TemplateId     = template.Id,
                    SlotId         = nodePoco.SlotId ?? string.Empty,
                    NodeTemplateId = string.IsNullOrEmpty(nodePoco.NodeTemplateId)
                                     ? Guid.Empty
                                     : FromSurrealIdStripped(nodePoco.NodeTemplateId),
                    ParamOverrides = string.IsNullOrEmpty(nodePoco.ParamOverrides)
                                     ? "{}"
                                     : nodePoco.ParamOverrides,
                    IsEntry        = nodePoco.IsEntry,
                    IsTerminal     = nodePoco.IsTerminal,
                });
            }
        }

        if (p.Edges is not null)
        {
            foreach (var edgePoco in p.Edges)
            {
                template.Edges.Add(new QuestTemplateEdge
                {
                    Id            = string.IsNullOrEmpty(edgePoco.Id)
                                    ? Guid.NewGuid()
                                    : SurrealId.FromSurrealId(edgePoco.Id),
                    TemplateId    = template.Id,
                    SourceSlotId  = edgePoco.SourceSlotId ?? string.Empty,
                    TargetSlotId  = edgePoco.TargetSlotId ?? string.Empty,
                    EdgeType      = ParseEdgeType(edgePoco.EdgeType),
                });
            }
        }

        return template;
    }

    private static QuestNodeTemplate ToDomain(QuestNodeTemplatePoco p) => new()
    {
        Id             = FromSurrealIdStripped(p.Id),
        Name           = p.Name ?? string.Empty,
        NodeType       = ParseNodeType(p.NodeType),
        Description    = p.Description,
        DefaultConfig  = string.IsNullOrEmpty(p.DefaultConfig) ? "{}" : p.DefaultConfig,
        ConfigSchema   = string.IsNullOrEmpty(p.ConfigSchema)  ? "{}" : p.ConfigSchema,
        InputSchema    = string.IsNullOrEmpty(p.InputSchema)   ? "{}" : p.InputSchema,
        OutputSchema   = string.IsNullOrEmpty(p.OutputSchema)  ? "{}" : p.OutputSchema,
        Version        = string.IsNullOrEmpty(p.Version) ? "1.0.0" : p.Version,
        AuthorAvatarId = string.IsNullOrEmpty(p.AuthorAvatarId)
                         ? Guid.Empty
                         : FromSurrealIdStripped(p.AuthorAvatarId),
        IsPublic       = p.IsPublic,
        Tags           = p.Tags?.ToList() ?? new List<string>(),
    };

    private static QuestNodeType ParseNodeType(string? raw) =>
        Enum.TryParse<QuestNodeType>(raw, ignoreCase: false, out var v)
            ? v
            : throw new InvalidOperationException(
                $"Unrecognised QuestNodeType '{raw}' read from SurrealDB. " +
                "Schema ASSERT INSIDE [...] should have prevented this; refresh the schema.");

    private static QuestEdgeType ParseEdgeType(string? raw) =>
        Enum.TryParse<QuestEdgeType>(raw, ignoreCase: false, out var v)
            ? v
            : QuestEdgeType.Control;

    // ── POCO (private) ────────────────────────────────────────────────────────

    private sealed class QuestTemplatePoco
    {
        [JsonPropertyName("id")]               public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")]             public string? Name { get; set; }
        [JsonPropertyName("description")]      public string? Description { get; set; }
        [JsonPropertyName("author_avatar_id")] public string AuthorAvatarId { get; set; } = string.Empty;
        [JsonPropertyName("parameters")]       public string? Parameters { get; set; }
        [JsonPropertyName("version")]          public string? Version { get; set; }
        [JsonPropertyName("is_public")]        public bool IsPublic { get; set; }
        [JsonPropertyName("nodes")]            public List<QuestTemplateNodePoco>? Nodes { get; set; }
        [JsonPropertyName("edges")]            public List<QuestTemplateEdgePoco>? Edges { get; set; }
        [JsonPropertyName("tags")]             public List<string>? Tags { get; set; }
    }

    private sealed class QuestTemplateNodePoco
    {
        [JsonPropertyName("id")]              public string Id { get; set; } = string.Empty;
        [JsonPropertyName("slot_id")]         public string? SlotId { get; set; }
        [JsonPropertyName("node_template_id")] public string NodeTemplateId { get; set; } = string.Empty;
        [JsonPropertyName("param_overrides")] public string? ParamOverrides { get; set; }
        [JsonPropertyName("is_entry")]        public bool IsEntry { get; set; }
        [JsonPropertyName("is_terminal")]     public bool IsTerminal { get; set; }
    }

    private sealed class QuestTemplateEdgePoco
    {
        [JsonPropertyName("id")]              public string Id { get; set; } = string.Empty;
        [JsonPropertyName("source_slot_id")]  public string? SourceSlotId { get; set; }
        [JsonPropertyName("target_slot_id")]  public string? TargetSlotId { get; set; }
        [JsonPropertyName("edge_type")]       public string? EdgeType { get; set; }
    }

    private sealed class QuestNodeTemplatePoco
    {
        [JsonPropertyName("id")]               public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")]             public string? Name { get; set; }
        [JsonPropertyName("node_type")]        public string? NodeType { get; set; }
        [JsonPropertyName("description")]      public string? Description { get; set; }
        [JsonPropertyName("default_config")]   public string? DefaultConfig { get; set; }
        [JsonPropertyName("config_schema")]    public string? ConfigSchema { get; set; }
        [JsonPropertyName("input_schema")]     public string? InputSchema { get; set; }
        [JsonPropertyName("output_schema")]    public string? OutputSchema { get; set; }
        [JsonPropertyName("version")]          public string? Version { get; set; }
        [JsonPropertyName("author_avatar_id")] public string AuthorAvatarId { get; set; } = string.Empty;
        [JsonPropertyName("is_public")]        public bool IsPublic { get; set; }
        [JsonPropertyName("tags")]             public List<string>? Tags { get; set; }
    }
}
