using System.Text.Json;
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
/// from the backend; <c>action_author</c> ultimately re-validates anyway.
/// </summary>
public sealed class ActionCatalogTool : IMcpTool
{
    // Tier-2 economic nodes require a tenant chain capability at execution time.
    private static readonly HashSet<ActionNodeType> ChainRequired = new()
    {
        ActionNodeType.Swap,
        ActionNodeType.Grant,
        ActionNodeType.Transfer,
        ActionNodeType.Refund,
        ActionNodeType.FungibleTokenCreate,
        // Fractionalization rails (final-hardening D1) reach the real cross-chain bridge.
        ActionNodeType.Bridge,
        ActionNodeType.Back,
    };

    private static readonly JsonElement _inputSchema;
    private static readonly JsonElement _catalog;

    static ActionCatalogTool()
    {
        using (var doc = JsonDocument.Parse("""{ "type": "object", "properties": {} }"""))
            _inputSchema = doc.RootElement.Clone();

        var nodeTypes = Enum.GetValues<ActionNodeType>()
            .Select(t => new
            {
                name = t.ToString(),
                requires_chain = ChainRequired.Contains(t),
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

        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(catalog)))
            _catalog = doc.RootElement.Clone();
    }

    public string Name        => "action_catalog";
    public string Description => "Return the action node-type vocabulary, edge types, and DAG structural rules. Call this before action_author to learn what nodes and shapes are valid.";
    public JsonElement InputSchema => _inputSchema;

    public Task<JsonElement> ExecuteAsync(ToolCallContext context, JsonElement args, CancellationToken ct)
        => Task.FromResult(_catalog);
}
