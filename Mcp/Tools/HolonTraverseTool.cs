using System.Text.Json;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Query;

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

            // AvatarId comes exclusively from context — privilege-escalation gate.
            // id/link comparisons bind the `table:hex` link form: the record id and
            // record<> link columns never equal a bare-hex string. See Mcp/AGENTS.md §record-id-binding.
            var avatarIdStr = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(context.AvatarId))!;
            var holonIdStr  = SurrealLink.ToLink("holon", SurrealId.ToSurrealId(holonId))!;

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
            {
                // peer_holon_ids persists as a native array<string> of bare-hex ids
                // (see SurrealHolonStore). Read it as a JSON array, not a string.
                var peerIds = ParsePeerIds(root.PeerHolonIds);
                foreach (var peerId in peerIds)
                {
                    if (string.IsNullOrEmpty(peerId)) continue;
                    // peer_holon_ids stores bare-hex ids (array<string>); bind the
                    // `holon:hex` link form so the record-id comparison matches.
                    var peerLink = peerId.Contains(':') ? peerId : SurrealLink.ToLink("holon", peerId);
                    var peerQ = SurrealQuery
                        .Of("SELECT id, name, description, parent_holon_id, avatar_id, provider_name, chain_id, asset_type, token_id, is_active FROM holon WHERE id = $pid")
                        .WithParam("pid", peerLink);

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
    /// peer_holon_ids is a native SurrealDB <c>array&lt;string&gt;</c> of bare-hex ids.
    /// Enumerate the JSON array element and return the ids.
    /// </summary>
    private static IEnumerable<string> ParsePeerIds(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Array } el) yield break;

        foreach (var item in el.EnumerateArray())
        {
            var s = item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText();
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
        // Native array<string> of bare-hex ids; enumerated in ParsePeerIds.
        [JsonPropertyName("peer_holon_ids")]  public JsonElement? PeerHolonIds { get; set; }
    }
}
