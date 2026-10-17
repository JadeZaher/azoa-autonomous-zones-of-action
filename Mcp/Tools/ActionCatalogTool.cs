using System.Text.Json;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Mcp.Tools;

/// <summary>
/// Read-only vocabulary tool for the "natural-language → DAG" flow. Returns the
/// full set of <see cref="ActionNodeType"/> values (the nodes an action may
/// contain), the valid <see cref="ActionEdgeType"/> values, and the structural
/// rules a DAG must satisfy. A calling model uses this to know what it can emit
/// before invoking <c>action_author</c>.
///
/// The node list is derived reflectively from the enum so it can never drift
/// from the backend, and <c>requires_chain</c> is read off the REGISTERED handler
/// for each node type (<see cref="IActionNodeHandler.RequiresChainCapability"/>) —
/// the same declaration the pre-execution capability gate enforces. A
/// hand-maintained tier list used to live here and had already gone stale, telling
/// callers a chain node was chain-free; <c>action_author</c> re-validates anyway,
/// but the catalog must not lie about what a node will reach.
/// </summary>
public sealed class ActionCatalogTool : IMcpTool
{
    private static readonly JsonElement _inputSchema;

    /// <summary>
    /// The catalog JSON, built once from the live handler set. Cached as a STRING,
    /// not a JsonElement: a reference assignment is atomic, whereas publishing a
    /// multi-field struct across threads can be read torn. The tier flags are
    /// compile-time declarations, so two racing builds produce identical text — a
    /// benign race costing one extra build, never a wrong answer.
    /// </summary>
    private static string? _catalogJson;

    static ActionCatalogTool()
    {
        using (var doc = JsonDocument.Parse("""{ "type": "object", "properties": {} }"""))
            _inputSchema = doc.RootElement.Clone();
    }

    /// <summary>
    /// Serializes the catalog, reading each node type's chain requirement from the
    /// handler that will actually execute it. A node type with no registered handler
    /// is reported <c>requires_chain: false</c> — it also cannot execute at all, and
    /// the handler registry fails fast on that at startup.
    /// </summary>
    private static string BuildCatalogJson(IReadOnlyCollection<IActionNodeHandler> handlers)
    {
        var chainRequired = handlers
            .Where(h => h.RequiresChainCapability)
            .Select(h => h.NodeType)
            .ToHashSet();

        var nodeTypes = Enum.GetValues<ActionNodeType>()
            .Select(t => new
            {
                name = t.ToString(),
                requires_chain = chainRequired.Contains(t),
            })
            .ToList();

        var edgeTypes = Enum.GetNames<ActionEdgeType>();

        var catalog = new
        {
            node_types = nodeTypes,
            edge_types = edgeTypes,
            dag_rules = new[]
            {
                "The graph must be acyclic (a DAG).",
                "At least one node must be marked is_entry=true and have no incoming edges.",
                "Every node with no incoming edges must be marked is_entry=true (otherwise it is an orphan).",
                "At least one leaf node (no outgoing edges) must be marked is_terminal=true.",
                "Every node must be reachable from an entry node.",
                "Edges reference nodes by their zero-based index in the nodes array (source_node_id / target_node_id are indices, not GUIDs).",
                "Self-loops (source index == target index) are not allowed.",
            },
            authoring_hint =
                "Build a DAG spec and submit it via the action_author tool. Each node carries a 'config' JSON string whose shape depends on its node type; leave unknown fields as sensible defaults or empty strings.",
        };

        return JsonSerializer.Serialize(catalog);
    }

    /// <summary>Detaches the element from its document so it outlives this call.</summary>
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public string Name        => "action_catalog";
    public string Description => "Return the action node-type vocabulary, edge types, and DAG structural rules. Call this before action_author to learn what nodes and shapes are valid.";
    public JsonElement InputSchema => _inputSchema;

    public Task<JsonElement> ExecuteAsync(ToolCallContext context, JsonElement args, CancellationToken ct)
    {
        if (_catalogJson is { } ready) return Task.FromResult(Parse(ready));

        var handlers = context.Services.GetServices<IActionNodeHandler>().ToList();
        var built = BuildCatalogJson(handlers);

        // Cache only a catalog built from a provider that actually resolved handlers.
        // Caching an empty-handler build would pin requires_chain:false for every node
        // for the life of the process — the stale-tier-set bug, made permanent.
        if (handlers.Count > 0) _catalogJson = built;
        return Task.FromResult(Parse(built));
    }
}
