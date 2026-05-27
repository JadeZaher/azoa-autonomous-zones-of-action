using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Oasis.SurrealDb.Client.Query;

namespace OASIS.WebAPI.Mcp.Tools;

/// <summary>
/// MCP tool that performs HNSW cosine-similarity semantic search over
/// <c>holon</c> or <c>quest</c> embeddings, scoped to the calling avatar.
///
/// <para>
/// Requires a registered <see cref="IEmbeddingProvider"/> in the DI container.
/// If only the placeholder <see cref="DeterministicDummyEmbeddingProvider"/> is
/// registered, search results are meaningless — see its XML doc for the
/// production-swap instructions.
/// </para>
/// </summary>
public sealed class VectorSearchTool : IMcpTool
{
    // ── Allowed tables (allowlist enforced before any DB call) ───────────────

    private static readonly HashSet<string> AllowedTables =
        new(StringComparer.OrdinalIgnoreCase) { "holon", "quest" };

    // ── Input schema (parsed once; property getter returns the cached element)

    private const string InputSchemaJson =
        """
        {
          "type": "object",
          "properties": {
            "query_text": { "type": "string", "minLength": 1, "maxLength": 4096 },
            "table":      { "type": "string", "enum": ["holon", "quest"], "default": "holon" },
            "k":          { "type": "integer", "default": 10, "minimum": 1, "maximum": 100 }
          },
          "required": ["query_text"]
        }
        """;

    private static readonly JsonElement _inputSchema;

    static VectorSearchTool()
    {
        using var doc = JsonDocument.Parse(InputSchemaJson);
        _inputSchema = doc.RootElement.Clone();
    }

    // ── Per-table SurrealQL templates (compile-time constants; never interpolated) ──
    //
    // Table names cannot be parameterised in SurrealQL, so we keep one
    // const string per table.  Both shapes are identical modulo the table
    // token; the analyzer (SRDB0001) is satisfied because no runtime value is
    // injected into the string — all runtime data flows through $-params.

    private const string HolonSearchSql =
        "SELECT id, name, vector::similarity::cosine(embedding, $q) AS score " +
        "FROM holon " +
        "WHERE avatar_id = $avatar_id AND embedding IS NOT NONE " +
        "ORDER BY score DESC LIMIT $k";

    private const string QuestSearchSql =
        "SELECT id, name, vector::similarity::cosine(embedding, $q) AS score " +
        "FROM quest " +
        "WHERE avatar_id = $avatar_id AND embedding IS NOT NONE " +
        "ORDER BY score DESC LIMIT $k";

    // ── IMcpTool ──────────────────────────────────────────────────────────────

    public string Name        => "vector_search";
    public string Description =>
        "HNSW semantic search over holon or quest embeddings. Returns top-k matches by cosine similarity.";
    public JsonElement InputSchema => _inputSchema;

    public async Task<JsonElement> ExecuteAsync(
        ToolCallContext context,
        JsonElement args,
        CancellationToken ct)
    {
        try
        {
            // ── Parse: query_text (required) ──────────────────────────────
            if (!args.TryGetProperty("query_text", out var queryTextEl))
                return ErrorResult("query_text is required.");

            var queryText = queryTextEl.GetString();
            if (string.IsNullOrWhiteSpace(queryText))
                return ErrorResult("query_text must not be empty.");

            // ── Parse: table (optional, default "holon") ──────────────────
            var table = "holon";
            if (args.TryGetProperty("table", out var tableEl))
                table = tableEl.GetString() ?? "holon";

            if (!AllowedTables.Contains(table))
                return ErrorResult($"table must be one of: {string.Join(", ", AllowedTables)}.");

            // ── Parse: k (optional, default 10) ───────────────────────────
            var k = 10;
            if (args.TryGetProperty("k", out var kEl) &&
                kEl.ValueKind == JsonValueKind.Number &&
                kEl.TryGetInt32(out var kParsed))
            {
                k = kParsed;
            }

            if (k < 1 || k > 100)
                return ErrorResult("k must be between 1 and 100.");

            // ── Embed the query text ───────────────────────────────────────
            var embedder = context.Services.GetRequiredService<IEmbeddingProvider>();
            var embedding = await embedder.EmbedAsync(queryText, ct);

            // ── AvatarId comes exclusively from context (privilege gate) ───
            var avatarIdStr = context.AvatarId.ToString("N").ToLowerInvariant();

            // ── Build the table-specific query ─────────────────────────────
            var sql = string.Equals(table, "holon", StringComparison.OrdinalIgnoreCase)
                ? HolonSearchSql
                : QuestSearchSql;

            SurrealQuery query;
            try
            {
                query = SurrealQuery
                    .Of(sql)
                    .WithParam("q", embedding)
                    .WithParam("avatar_id", avatarIdStr)
                    .WithParam("k", k);
            }
            catch (Exception ex)
            {
                return ErrorResult("internal", $"Query construction failed: {ex.Message}");
            }

            // ── Execute ────────────────────────────────────────────────────
            IReadOnlyList<VectorMatchPoco> rows;
            try
            {
                rows = await context.Executor.QueryAsync<VectorMatchPoco>(query, ct);
            }
            catch (Exception ex)
            {
                // The HNSW index may be absent on a fresh test namespace — the query
                // degrades to a linear scan in that case, which is fine for dev.
                // Log a warning if we can surface one, but never fail the caller.
                var logger = context.Services.GetService<ILogger<VectorSearchTool>>();
                logger?.LogWarning(
                    "vector_search on table '{Table}' raised an exception (HNSW index may be absent, " +
                    "degrading to linear scan is expected on fresh namespaces): {Message}",
                    table, ex.Message);

                return ErrorResult("internal", ex.Message);
            }

            // ── Shape the response ─────────────────────────────────────────
            var matches = rows.Select(r => new
            {
                id    = r.Id   ?? string.Empty,
                name  = r.Name ?? string.Empty,
                score = r.Score
            }).ToList();

            return ToJsonElement(new
            {
                matches,
                table    = table.ToLowerInvariant(),
                k_actual = matches.Count
            });
        }
        catch (Exception ex)
        {
            return ErrorResult("internal", ex.Message);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static JsonElement ErrorResult(string message, string? detail = null)
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

    // ── Private POCOs ─────────────────────────────────────────────────────────

    private sealed class VectorMatchPoco
    {
        [JsonPropertyName("id")]    public string? Id    { get; set; }
        [JsonPropertyName("name")]  public string? Name  { get; set; }
        [JsonPropertyName("score")] public float   Score { get; set; }
    }
}
