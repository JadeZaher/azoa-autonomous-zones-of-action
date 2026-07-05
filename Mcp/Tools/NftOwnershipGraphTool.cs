using System.Text.Json;
using System.Text.Json.Serialization;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Mcp.Tools;

/// <summary>
/// Returns all NFT holons owned by the calling avatar, optionally filtered
/// by chain.  The avatar_id is sourced exclusively from the
/// <see cref="ToolCallContext"/> — never from a tool input parameter.
/// </summary>
public sealed class NftOwnershipGraphTool : IMcpTool
{
    // ── Schema ────────────────────────────────────────────────────────────────

    private const string InputSchemaJson =
        """
        {
          "type": "object",
          "properties": {
            "chain_id": { "type": "string" }
          }
        }
        """;

    private static readonly JsonElement _inputSchema;

    static NftOwnershipGraphTool()
    {
        using var doc = JsonDocument.Parse(InputSchemaJson);
        _inputSchema = doc.RootElement.Clone();
    }

    // ── IMcpTool ──────────────────────────────────────────────────────────────

    public string Name        => "nft_ownership_graph";
    public string Description => "Return all NFT holons owned by the calling avatar, optionally filtered by chain.";
    public JsonElement InputSchema => _inputSchema;

    public async Task<JsonElement> ExecuteAsync(
        ToolCallContext context,
        JsonElement args,
        CancellationToken ct)
    {
        try
        {
            // AvatarId comes exclusively from context — privilege-escalation gate (line 43)
            var avatarIdStr = SurrealId.ToSurrealId(context.AvatarId);

            // Optional chain filter — null means "all chains"
            string? chainFilter = null;
            if (args.TryGetProperty("chain_id", out var chainEl))
                chainFilter = chainEl.GetString();

            // Build query: parameterized chain conditional using SurrealQL NONE idiom.
            // When chain param is null we bind $chain as the literal NONE token so
            // the expression ($chain == NONE OR chain_id = $chain) passes every row.
            // Because SurrealQL cannot bind NONE as a parameter value directly, we
            // instead use two separate query paths to keep the SQL constant.
            IReadOnlyList<NftHolonPoco> rows;

            if (string.IsNullOrEmpty(chainFilter))
            {
                // No chain filter — return all NFTs for this avatar
                var q = SurrealQuery
                    .Of("SELECT id, name, chain_id, provider_name, token_id, asset_type FROM holon WHERE avatar_id = $avatar_id AND asset_type = 'NFT' ORDER BY chain_id, name")
                    .WithParam("avatar_id", avatarIdStr);

                rows = await context.Executor.QueryAsync<NftHolonPoco>(q, ct);
            }
            else
            {
                // Chain-filtered path
                var q = SurrealQuery
                    .Of("SELECT id, name, chain_id, provider_name, token_id, asset_type FROM holon WHERE avatar_id = $avatar_id AND asset_type = 'NFT' AND chain_id = $chain ORDER BY chain_id, name")
                    .WithParam("avatar_id", avatarIdStr)
                    .WithParam("chain", chainFilter);

                rows = await context.Executor.QueryAsync<NftHolonPoco>(q, ct);
            }

            // ── Build flat list ───────────────────────────────────────────
            var nfts = rows.Select(r => new
            {
                id           = FromSurrealId(r.Id).ToString(),
                name         = r.Name ?? string.Empty,
                chain_id     = r.ChainId,
                provider     = r.ProviderName,
                token_id     = r.TokenId
            }).ToList();

            // ── Build by_chain group ──────────────────────────────────────
            var byChain = nfts
                .GroupBy(n => n.chain_id ?? "unknown")
                .ToDictionary(g => g.Key, g => (object)g.ToList());

            return ToJsonElement(new
            {
                nfts,
                by_chain = byChain
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

    private static JsonElement ToJsonElement<T>(T value)
    {
        var raw = JsonSerializer.Serialize(value);
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    // ── Private POCO ─────────────────────────────────────────────────────────

    private sealed class NftHolonPoco
    {
        [JsonPropertyName("id")]            public string  Id           { get; set; } = string.Empty;
        [JsonPropertyName("name")]          public string? Name         { get; set; }
        [JsonPropertyName("chain_id")]      public string? ChainId      { get; set; }
        [JsonPropertyName("provider_name")] public string? ProviderName { get; set; }
        [JsonPropertyName("token_id")]      public string? TokenId      { get; set; }
        [JsonPropertyName("asset_type")]    public string? AssetType    { get; set; }
    }
}
