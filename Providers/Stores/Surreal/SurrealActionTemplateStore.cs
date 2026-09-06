using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IActionTemplateStore"/>.
///
/// Pattern: mirrors <see cref="SurrealApiKeyStore"/> — Guid("N") lowercase-hex
/// record ids and inline POCO until the source-generator catches up to tables
/// 130/140. Replace the inline POCOs with the generated types when those
/// arrive.
///
/// Storage shape: template Nodes + Edges embed as FLEXIBLE array<object>
/// fields on the parent action_template row so a template fetch is a single
/// record-id SELECT (no join). Sub-shapes (slot id, node-template id, edge
/// source/target) are validated by the C# POCO at deserialisation; the
/// SurrealDB FLEXIBLE field accepts arbitrary objects so the schema does not
/// need to be amended when the slot record grows new fields.
/// </summary>
public sealed class SurrealActionTemplateStore : IActionTemplateStore
{
    private const string TemplateTable = "action_template";
    private const string NodeTemplateTable = "action_node_template";

    private readonly ISurrealExecutor _executor;

    public SurrealActionTemplateStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<ActionTemplate?> GetTemplateAsync(Guid templateId, CancellationToken ct)
    {
        var surrealId = SurrealId.ToSurrealId(templateId);

        var q = SurrealQuery
            .Of("SELECT * FROM type::record($_t, $_id)")
            .WithParam("_t", TemplateTable)
            .WithParam("_id", surrealId);

        var rows = await _executor.QueryAsync<ActionTemplatePoco>(q, ct);
        return rows.Count == 0 ? null : ToDomain(rows[0]);
    }

    public async Task<ActionNodeTemplate?> GetNodeTemplateAsync(Guid nodeTemplateId, CancellationToken ct)
    {
        var surrealId = SurrealId.ToSurrealId(nodeTemplateId);

        var q = SurrealQuery
            .Of("SELECT * FROM type::record($_t, $_id)")
            .WithParam("_t", NodeTemplateTable)
            .WithParam("_id", surrealId);

        var rows = await _executor.QueryAsync<ActionNodeTemplatePoco>(q, ct);
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

    private static ActionTemplate ToDomain(ActionTemplatePoco p)
    {
        var template = new ActionTemplate
        {
            // SELECT * returns the record id in full `table:<hex>` form; strip the
            // table prefix before parsing the bare 32-hex Guid.
            Id             = FromSurrealIdStripped(p.Id),
            Name           = p.Name ?? string.Empty,
            Description    = p.Description,
            AuthorUserId = string.IsNullOrEmpty(p.AuthorUserId)
                             ? Guid.Empty
                             : FromSurrealIdStripped(p.AuthorUserId),
            Parameters     = string.IsNullOrEmpty(p.Parameters) ? "{}" : p.Parameters,
            Version        = string.IsNullOrEmpty(p.Version) ? "1.0.0" : p.Version,
            IsPublic       = p.IsPublic,
            Tags           = p.Tags?.ToList() ?? new List<string>(),
        };

        if (p.Nodes is not null)
        {
            foreach (var nodePoco in p.Nodes)
            {
                template.Nodes.Add(new ActionTemplateNode
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
                template.Edges.Add(new ActionTemplateEdge
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

    private static ActionNodeTemplate ToDomain(ActionNodeTemplatePoco p) => new()
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
        AuthorUserId = string.IsNullOrEmpty(p.AuthorUserId)
                         ? Guid.Empty
                         : FromSurrealIdStripped(p.AuthorUserId),
        IsPublic       = p.IsPublic,
        Tags           = p.Tags?.ToList() ?? new List<string>(),
    };

    private static ActionNodeType ParseNodeType(string? raw) =>
        Enum.TryParse<ActionNodeType>(raw, ignoreCase: false, out var v)
            ? v
            : throw new InvalidOperationException(
                $"Unrecognised ActionNodeType '{raw}' read from SurrealDB. " +
                "Schema ASSERT INSIDE [...] should have prevented this; refresh the schema.");

    private static ActionEdgeType ParseEdgeType(string? raw) =>
        Enum.TryParse<ActionEdgeType>(raw, ignoreCase: false, out var v)
            ? v
            : ActionEdgeType.Control;

    // ── POCO (private) ────────────────────────────────────────────────────────

    private sealed class ActionTemplatePoco
    {
        [JsonPropertyName("id")]               public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")]             public string? Name { get; set; }
        [JsonPropertyName("description")]      public string? Description { get; set; }
        [JsonPropertyName("author_user_id")] public string AuthorUserId { get; set; } = string.Empty;
        [JsonPropertyName("parameters")]       public string? Parameters { get; set; }
        [JsonPropertyName("version")]          public string? Version { get; set; }
        [JsonPropertyName("is_public")]        public bool IsPublic { get; set; }
        [JsonPropertyName("nodes")]            public List<ActionTemplateNodePoco>? Nodes { get; set; }
        [JsonPropertyName("edges")]            public List<ActionTemplateEdgePoco>? Edges { get; set; }
        [JsonPropertyName("tags")]             public List<string>? Tags { get; set; }
    }

    private sealed class ActionTemplateNodePoco
    {
        [JsonPropertyName("id")]              public string Id { get; set; } = string.Empty;
        [JsonPropertyName("slot_id")]         public string? SlotId { get; set; }
        [JsonPropertyName("node_template_id")] public string NodeTemplateId { get; set; } = string.Empty;
        [JsonPropertyName("param_overrides")] public string? ParamOverrides { get; set; }
        [JsonPropertyName("is_entry")]        public bool IsEntry { get; set; }
        [JsonPropertyName("is_terminal")]     public bool IsTerminal { get; set; }
    }

    private sealed class ActionTemplateEdgePoco
    {
        [JsonPropertyName("id")]              public string Id { get; set; } = string.Empty;
        [JsonPropertyName("source_slot_id")]  public string? SourceSlotId { get; set; }
        [JsonPropertyName("target_slot_id")]  public string? TargetSlotId { get; set; }
        [JsonPropertyName("edge_type")]       public string? EdgeType { get; set; }
    }

    private sealed class ActionNodeTemplatePoco
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
        [JsonPropertyName("author_user_id")] public string AuthorUserId { get; set; } = string.Empty;
        [JsonPropertyName("is_public")]        public bool IsPublic { get; set; }
        [JsonPropertyName("tags")]             public List<string>? Tags { get; set; }
    }
}
