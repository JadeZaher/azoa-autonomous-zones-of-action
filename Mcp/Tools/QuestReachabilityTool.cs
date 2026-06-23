using System.Text.Json;
using System.Text.Json.Serialization;
using Azoa.SurrealDb.Client.Query;

namespace AZOA.WebAPI.Mcp.Tools;

/// <summary>
/// Returns the set of quest nodes reachable from a starting node via
/// control-flow edges, scoped to a single quest definition owned by the
/// calling avatar.
/// </summary>
public sealed class QuestReachabilityTool : IMcpTool
{
    // ── Schema (parsed once; property getter returns the cached element) ───────

    private const string InputSchemaJson =
        """
        {
          "type": "object",
          "properties": {
            "quest_id":    { "type": "string", "format": "uuid" },
            "from_node_id": { "type": "string", "format": "uuid" }
          },
          "required": ["quest_id", "from_node_id"]
        }
        """;

    private static readonly JsonElement _inputSchema;

    static QuestReachabilityTool()
    {
        using var doc = JsonDocument.Parse(InputSchemaJson);
        _inputSchema = doc.RootElement.Clone();
    }

    // ── IMcpTool ──────────────────────────────────────────────────────────────

    public string Name        => "quest_reachability";
    public string Description => "Return the set of quest nodes reachable from a starting node via control-flow edges, scoped to a single quest definition.";
    public JsonElement InputSchema => _inputSchema;

    public async Task<JsonElement> ExecuteAsync(
        ToolCallContext context,
        JsonElement args,
        CancellationToken ct)
    {
        try
        {
            // ── Parse inputs ──────────────────────────────────────────────
            if (!args.TryGetProperty("quest_id", out var questIdEl) ||
                !Guid.TryParse(questIdEl.GetString(), out var questId))
                return Error("quest_id is required and must be a valid UUID.");

            if (!args.TryGetProperty("from_node_id", out var fromNodeEl) ||
                !Guid.TryParse(fromNodeEl.GetString(), out var fromNodeId))
                return Error("from_node_id is required and must be a valid UUID.");

            var questIdStr   = ToSurrealId(questId);
            var fromNodeStr  = ToSurrealId(fromNodeId);
            // AvatarId comes exclusively from context — privilege-escalation gate (line 62)
            var avatarIdStr  = ToSurrealId(context.AvatarId);

            // ── Ownership check: fetch quest head ─────────────────────────
            var questQ = SurrealQuery
                .Of("SELECT id, avatar_id FROM quest WHERE id = $quest_id")
                .WithParam("quest_id", questIdStr);

            var questRows = await context.Executor.QueryAsync<QuestOwnerPoco>(questQ, ct);
            if (questRows.Count == 0)
                return Error("quest not found.");

            if (questRows[0].AvatarId != avatarIdStr)
                return Forbidden();

            // ── Fetch nodes + edges in one combined round-trip ────────────
            var nodesQ = SurrealQuery
                .Of("SELECT id, name, execution_order FROM quest_node WHERE quest_id = $qid ORDER BY execution_order ASC")
                .WithParam("qid", questIdStr);

            var edgesQ = SurrealQuery
                .Of("SELECT source_node_id, target_node_id FROM quest_edge WHERE quest_id = $qid")
                .WithParam("qid", questIdStr);

            var combined = SurrealQuery.Combine(nodesQ, edgesQ);
            var resp = await context.Executor.ExecuteAsync(combined, ct);
            resp.EnsureAllOk();

            var nodeRows = resp.GetValues<QuestNodePoco>(0);
            var edgeRows = resp.GetValues<QuestEdgePoco>(1);

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

    private static string ToSurrealId(Guid id) => id.ToString("N").ToLowerInvariant();

    private static Guid FromSurrealId(string raw)
    {
        // Strip table prefix (e.g. "quest_node:abc123" → "abc123")
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

    private sealed class QuestOwnerPoco
    {
        [JsonPropertyName("id")]        public string Id       { get; set; } = string.Empty;
        [JsonPropertyName("avatar_id")] public string AvatarId { get; set; } = string.Empty;
    }

    private sealed class QuestNodePoco
    {
        [JsonPropertyName("id")]              public string Id             { get; set; } = string.Empty;
        [JsonPropertyName("name")]            public string? Name          { get; set; }
        [JsonPropertyName("execution_order")] public int ExecutionOrder    { get; set; }
    }

    private sealed class QuestEdgePoco
    {
        [JsonPropertyName("source_node_id")] public string SourceNodeId { get; set; } = string.Empty;
        [JsonPropertyName("target_node_id")] public string TargetNodeId { get; set; } = string.Empty;
    }
}
