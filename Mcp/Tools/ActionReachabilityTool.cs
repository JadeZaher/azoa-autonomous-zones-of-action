using System.Text.Json;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Core.Surreal;

namespace AZOA.WebAPI.Mcp.Tools;

/// <summary>
/// Returns the set of action nodes reachable from a starting node via
/// control-flow edges, scoped to a single action definition owned by the
/// calling user.
/// </summary>
public sealed class ActionReachabilityTool : IMcpTool
{
    // ── Schema (parsed once; property getter returns the cached element) ───────

    private const string InputSchemaJson =
        """
        {
          "type": "object",
          "properties": {
            "action_id":    { "type": "string", "format": "uuid" },
            "from_node_id": { "type": "string", "format": "uuid" }
          },
          "required": ["action_id", "from_node_id"]
        }
        """;

    private static readonly JsonElement _inputSchema;

    static ActionReachabilityTool()
    {
        using var doc = JsonDocument.Parse(InputSchemaJson);
        _inputSchema = doc.RootElement.Clone();
    }

    // ── IMcpTool ──────────────────────────────────────────────────────────────

    public string Name        => "action_reachability";
    public string Description => "Return the set of action nodes reachable from a starting node via control-flow edges, scoped to a single action definition.";
    public JsonElement InputSchema => _inputSchema;

    public async Task<JsonElement> ExecuteAsync(
        ToolCallContext context,
        JsonElement args,
        CancellationToken ct)
    {
        try
        {
            // ── Parse inputs ──────────────────────────────────────────────
            if (!args.TryGetProperty("action_id", out var actionIdEl) ||
                !Guid.TryParse(actionIdEl.GetString(), out var actionId))
                return Error("action_id is required and must be a valid UUID.");

            if (!args.TryGetProperty("from_node_id", out var fromNodeEl) ||
                !Guid.TryParse(fromNodeEl.GetString(), out var fromNodeId))
                return Error("from_node_id is required and must be a valid UUID.");

            // Bind the `table:hex` link form: record ids and record<> link columns
            // never equal a bare-hex string. See Mcp/AGENTS.md §record-id-binding.
            var actionIdStr   = SurrealLink.ToLink("action", SurrealId.ToSurrealId(actionId))!;
            var fromNodeStr  = SurrealLink.ToLink("action_node", SurrealId.ToSurrealId(fromNodeId))!;
            // UserId comes exclusively from context — privilege-escalation gate
            var userIdStr  = SurrealLink.ToLink("user", SurrealId.ToSurrealId(context.UserId))!;

            // ── Ownership check: fetch action head ─────────────────────────
            // raw: narrow projection — reachability needs ids and ordering columns only.
            var actionQ = SurrealQuery
                .Of("SELECT id, user_id FROM action WHERE id = $action_id")
                .WithParam("action_id", SurrealRecordParam.OfLink(actionIdStr));

            var actionRows = await context.Executor.QueryAsync<ActionOwnerPoco>(actionQ, ct);
            if (actionRows.Count == 0)
                return Error("action not found.");

            if (actionRows[0].UserId != userIdStr)
                return Forbidden();

            // ── Fetch nodes + edges in one combined round-trip ────────────
            // raw: narrow projection — reachability needs ids and ordering columns only.
            var nodesQ = SurrealQuery
                .Of("SELECT id, name, execution_order FROM action_node WHERE action_id = $qid ORDER BY execution_order ASC")
                .WithParam("qid", SurrealRecordParam.OfLink(actionIdStr));

            // raw: narrow projection — reachability needs ids and ordering columns only.
            var edgesQ = SurrealQuery
                .Of("SELECT source_node_id, target_node_id FROM action_edge WHERE action_id = $qid")
                .WithParam("qid", SurrealRecordParam.OfLink(actionIdStr));

            var combined = SurrealQuery.Combine(nodesQ, edgesQ);
            var resp = await context.Executor.ExecuteAsync(combined, ct);
            resp.EnsureAllOk();

            var nodeRows = resp.GetValues<ActionNodePoco>(0);
            var edgeRows = resp.GetValues<ActionEdgePoco>(1);

            // ── BFS from from_node_id ─────────────────────────────────────
            var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var e in edgeRows)
            {
                if (!adjacency.TryGetValue(e.SourceNodeId, out var targets))
                {
                    targets = new List<string>();
                    adjacency[e.SourceNodeId] = targets;
                }
                targets.Add(e.TargetNodeId);
            }

            var reachable = new HashSet<string>(StringComparer.Ordinal);
            var queue     = new Queue<string>();
            queue.Enqueue(fromNodeStr);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!reachable.Add(current)) continue;
                if (adjacency.TryGetValue(current, out var neighbors))
                    foreach (var n in neighbors)
                        queue.Enqueue(n);
            }

            // ── Build ordered_nodes output (only reachable nodes) ─────────
            var orderedNodes = nodeRows
                .Where(n => reachable.Contains(n.Id))
                .OrderBy(n => n.ExecutionOrder)
                .Select(n => new
                {
                    id              = FromSurrealId(n.Id).ToString(),
                    name            = n.Name ?? string.Empty,
                    execution_order = n.ExecutionOrder
                })
                .ToList();

            var reachableGuids = reachable
                .Select(id => FromSurrealId(id).ToString())
                .OrderBy(x => x)
                .ToList();

            return ToJsonElement(new
            {
                reachable_node_ids = reachableGuids,
                ordered_nodes      = orderedNodes
            });
        }
        catch (Exception ex)
        {
            return Error("internal", ex.Message);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────


    private static Guid FromSurrealId(string raw)
    {
        // Strip table prefix (e.g. "action_node:abc123" → "abc123")
        var colon = raw.IndexOf(':');
        var hex = colon >= 0 ? raw[(colon + 1)..].Trim('⟨', '⟩') : raw;
        return Guid.TryParseExact(hex, "N", out var g) ? g : Guid.Empty;
    }

    private static JsonElement Error(string message, string? detail = null)
    {
        var obj = detail is null
            ? (object)new { error = message }
            : new { error = message, detail };
        return ToJsonElement(obj);
    }

    private static JsonElement Forbidden() =>
        ToJsonElement(new { error = "forbidden" });

    private static JsonElement ToJsonElement<T>(T value)
    {
        var raw = JsonSerializer.Serialize(value);
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    // ── Private POCOs ─────────────────────────────────────────────────────────

    private sealed class ActionOwnerPoco
    {
        [JsonPropertyName("id")]        public string Id       { get; set; } = string.Empty;
        [JsonPropertyName("user_id")] public string UserId { get; set; } = string.Empty;
    }

    private sealed class ActionNodePoco
    {
        [JsonPropertyName("id")]              public string Id             { get; set; } = string.Empty;
        [JsonPropertyName("name")]            public string? Name          { get; set; }
        [JsonPropertyName("execution_order")] public int ExecutionOrder    { get; set; }
    }

    private sealed class ActionEdgePoco
    {
        [JsonPropertyName("source_node_id")] public string SourceNodeId { get; set; } = string.Empty;
        [JsonPropertyName("target_node_id")] public string TargetNodeId { get; set; } = string.Empty;
    }
}
