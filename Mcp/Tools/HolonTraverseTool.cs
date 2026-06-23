using System.Text.Json;
using System.Text.Json.Serialization;
using Azoa.SurrealDb.Client.Query;

namespace AZOA.WebAPI.Mcp.Tools;

/// <summary>
/// Walks the holon polyhierarchy from a starting holon, returning the
/// parent chain, child subtree, and peers — all scoped to the calling avatar.
/// </summary>
public sealed class HolonTraverseTool : IMcpTool
{
    // ── Schema ────────────────────────────────────────────────────────────────

    private const string InputSchemaJson =
        """
        {
          "type": "object",
          "properties": {
            "holon_id":  { "type": "string", "format": "uuid" },
            "max_depth": { "type": "integer", "default": 3, "minimum": 1, "maximum": 10 }
          },
          "required": ["holon_id"]
        }
        """;

    private static readonly JsonElement _inputSchema;

    static HolonTraverseTool()
    {
        using var doc = JsonDocument.Parse(InputSchemaJson);
        _inputSchema = doc.RootElement.Clone();
    }

    // ── IMcpTool ──────────────────────────────────────────────────────────────

    public string Name        => "holon_traverse";
    public string Description => "Walk the holon polyhierarchy from a starting holon, returning parent chain + child subtree + peers.";
    public JsonElement InputSchema => _inputSchema;

    public async Task<JsonElement> ExecuteAsync(
        ToolCallContext context,
        JsonElement args,
        CancellationToken ct)
    {
        try
        {
            // ── Parse inputs ──────────────────────────────────────────────
            if (!args.TryGetProperty("holon_id", out var holonIdEl) ||
                !Guid.TryParse(holonIdEl.GetString(), out var holonId))
                return Error("holon_id is required and must be a valid UUID.");

            int maxDepth = 3;
            if (args.TryGetProperty("max_depth", out var mdEl) && mdEl.ValueKind == JsonValueKind.Number)
            {
                maxDepth = Math.Clamp(mdEl.GetInt32(), 1, 10);
            }

            // AvatarId comes exclusively from context — privilege-escalation gate (line 58)
            var avatarIdStr = ToSurrealId(context.AvatarId);
            var holonIdStr  = ToSurrealId(holonId);

            // ── Fetch root holon ──────────────────────────────────────────
            var rootQ = SurrealQuery
                .Of("SELECT id, name, description, parent_holon_id, avatar_id, provider_name, chain_id, asset_type, token_id, is_active, peer_holon_ids FROM holon WHERE id = $hid")
                .WithParam("hid", holonIdStr);

            var rootRows = await context.Executor.QueryAsync<HolonPoco>(rootQ, ct);
            if (rootRows.Count == 0)
                return Error("holon not found.");

            var root = rootRows[0];

            // Avatar ownership check
            if (root.AvatarId != avatarIdStr)
                return Forbidden();

            // ── Walk parent chain (up to maxDepth) ────────────────────────
            var ancestors = new List<object>();
            var current   = root.ParentHolonId;
            int depth     = 0;

            while (!string.IsNullOrEmpty(current) && depth < maxDepth)
            {
                var parentQ = SurrealQuery
                    .Of("SELECT id, name, description, parent_holon_id, avatar_id, provider_name, chain_id, asset_type, token_id, is_active FROM holon WHERE id = $pid")
                    .WithParam("pid", current);

                var parentRows = await context.Executor.QueryAsync<HolonPoco>(parentQ, ct);
                if (parentRows.Count == 0) break;

                var p = parentRows[0];
                ancestors.Add(MapHolon(p));
                current = p.ParentHolonId;
                depth++;
            }

            // ── Walk descendant subtree (BFS, up to maxDepth) ─────────────
            var descendants = new List<object>();
            await CollectDescendantsAsync(context.Executor, holonIdStr, descendants, maxDepth, 0, ct);

            // ── Fetch peer holons ─────────────────────────────────────────
            var peers = new List<object>();
            if (!string.IsNullOrEmpty(root.PeerHolonIds))
            {
                // peer_holon_ids is stored as a JSON array of id strings;
                // we decoded a comma-joined string from the POCO; parse individually.
                var peerIds = ParsePeerIds(root.PeerHolonIds);
                foreach (var peerId in peerIds)
                {
                    if (string.IsNullOrEmpty(peerId)) continue;
                    var peerQ = SurrealQuery
                        .Of("SELECT id, name, description, parent_holon_id, avatar_id, provider_name, chain_id, asset_type, token_id, is_active FROM holon WHERE id = $pid")
                        .WithParam("pid", peerId);

                    var peerRows = await context.Executor.QueryAsync<HolonPoco>(peerQ, ct);
                    if (peerRows.Count > 0)
                        peers.Add(MapHolon(peerRows[0]));
                }
            }

            return ToJsonElement(new
            {
                node        = MapHolon(root),
                ancestors,
                descendants,
                peers
            });
        }
        catch (Exception ex)
        {
            return Error("internal", ex.Message);
        }
    }

    // ── Recursively collect descendant holons via BFS ─────────────────────────

    private static async Task CollectDescendantsAsync(
        ISurrealExecutor executor,
        string parentId,
        List<object> accumulator,
        int maxDepth,
        int currentDepth,
        CancellationToken ct)
    {
        if (currentDepth >= maxDepth) return;

        var childQ = SurrealQuery
            .Of("SELECT id, name, description, parent_holon_id, avatar_id, provider_name, chain_id, asset_type, token_id, is_active FROM holon WHERE parent_holon_id = $pid")
            .WithParam("pid", parentId);

        var childRows = await executor.QueryAsync<HolonPoco>(childQ, ct);

        foreach (var child in childRows)
        {
            accumulator.Add(MapHolon(child));
            await CollectDescendantsAsync(executor, child.Id, accumulator, maxDepth, currentDepth + 1, ct);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ToSurrealId(Guid id) => id.ToString("N").ToLowerInvariant();

    private static Guid FromSurrealId(string raw)
    {
        var colon = raw.IndexOf(':');
        var hex = colon >= 0 ? raw[(colon + 1)..].Trim('⟨', '⟩') : raw;
        return Guid.TryParseExact(hex, "N", out var g) ? g : Guid.Empty;
    }

    private static object MapHolon(HolonPoco p) => new
    {
        id            = FromSurrealId(p.Id).ToString(),
        name          = p.Name ?? string.Empty,
        description   = p.Description,
        parent_holon_id = string.IsNullOrEmpty(p.ParentHolonId)
                          ? null
                          : (string?)FromSurrealId(p.ParentHolonId).ToString(),
        avatar_id     = string.IsNullOrEmpty(p.AvatarId)
                        ? null
                        : (string?)FromSurrealId(p.AvatarId).ToString(),
        provider_name = p.ProviderName,
        chain_id      = p.ChainId,
        asset_type    = p.AssetType,
        token_id      = p.TokenId,
        is_active     = p.IsActive
    };

    /// <summary>
    /// The POCO stores peer_holon_ids as a raw JSON string (e.g. <c>["abc","def"]</c>).
    /// Parse it as an array and return the ids.
    /// </summary>
    private static IEnumerable<string> ParsePeerIds(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        JsonElement el;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            el = doc.RootElement.Clone();
        }
        catch { yield break; }

        if (el.ValueKind == JsonValueKind.Array)
            foreach (var item in el.EnumerateArray())
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s)) yield return s;
            }
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

    // ── Private POCO ─────────────────────────────────────────────────────────

    private sealed class HolonPoco
    {
        [JsonPropertyName("id")]              public string  Id             { get; set; } = string.Empty;
        [JsonPropertyName("name")]            public string? Name           { get; set; }
        [JsonPropertyName("description")]     public string? Description    { get; set; }
        [JsonPropertyName("parent_holon_id")] public string? ParentHolonId  { get; set; }
        [JsonPropertyName("avatar_id")]       public string? AvatarId       { get; set; }
        [JsonPropertyName("provider_name")]   public string? ProviderName   { get; set; }
        [JsonPropertyName("chain_id")]        public string? ChainId        { get; set; }
        [JsonPropertyName("asset_type")]      public string? AssetType      { get; set; }
        [JsonPropertyName("token_id")]        public string? TokenId        { get; set; }
        [JsonPropertyName("is_active")]       public bool    IsActive       { get; set; } = true;
        // Stored as a JSON-encoded array string; we parse it ourselves.
        [JsonPropertyName("peer_holon_ids")]  public string? PeerHolonIds   { get; set; }
    }
}
