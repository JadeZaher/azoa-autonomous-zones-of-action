using System.Text.Json;
using Azoa.SurrealDb.Client.Query;

namespace AZOA.WebAPI.Mcp.Tools;

/// <summary>
/// Generic avatar-scoped read query against an allowlisted table with
/// parameterized filters.
///
/// Security design:
/// - Table name is only ever substituted from a private static readonly
///   <see cref="TableAllowlist"/> HashSet; no user input flows into the
///   SQL template string (SRDB0001 / G3 compliant).
/// - avatar_id is sourced exclusively from <see cref="ToolCallContext.AvatarId"/>
///   — it is never accepted as a tool input parameter.
/// - Filter keys are validated against <see cref="FilterAllowlist"/> before
///   being used as SurrealQL parameter names; unknown keys are rejected.
/// </summary>
public sealed class AvatarScopedQueryTool : IMcpTool
{
    // ── Schema ────────────────────────────────────────────────────────────────

    private const string InputSchemaJson =
        """
        {
          "type": "object",
          "properties": {
            "table": {
              "type": "string",
              "enum": ["wallet","holon","quest","quest_run","nft_ownership","operation_log"]
            },
            "filters": {
              "type": "object",
              "additionalProperties": { "type": ["string","number","boolean"] }
            },
            "limit": {
              "type": "integer",
              "default": 50,
              "minimum": 1,
              "maximum": 500
            }
          },
          "required": ["table"]
        }
        """;

    private static readonly JsonElement _inputSchema;

    static AvatarScopedQueryTool()
    {
        using var doc = JsonDocument.Parse(InputSchemaJson);
        _inputSchema = doc.RootElement.Clone();
    }

    // ── Allowlists ────────────────────────────────────────────────────────────

    /// <summary>
    /// Tables the tool may query.  The table name is substituted at the C#
    /// string level ONLY from this set — user input never reaches the SQL body.
    /// </summary>
    private static readonly HashSet<string> TableAllowlist =
        new(StringComparer.Ordinal)
        {
            "wallet",
            "holon",
            "quest",
            "quest_run",
            "nft_ownership",
            "operation_log"
        };

    /// <summary>
    /// Common-denominator filter fields allowed across all tables (v1).
    /// Keys not in this set are rejected with an error response.
    /// </summary>
    private static readonly HashSet<string> FilterAllowlist =
        new(StringComparer.Ordinal)
        {
            "name",
            "status",
            "chain_id",
            "asset_type",
            "created_date"
        };

    // ── IMcpTool ──────────────────────────────────────────────────────────────

    public string Name        => "avatar_scoped_query";
    public string Description => "Generic avatar-scoped read query against an allowlisted table with parameterized filters.";
    public JsonElement InputSchema => _inputSchema;

    public async Task<JsonElement> ExecuteAsync(
        ToolCallContext context,
        JsonElement args,
        CancellationToken ct)
    {
        try
        {
            // ── Validate table ────────────────────────────────────────────
            if (!args.TryGetProperty("table", out var tableEl))
                return Error("table is required.");

            var tableName = tableEl.GetString() ?? string.Empty;
            if (!TableAllowlist.Contains(tableName))
                return Error("table_not_allowed");

            // ── Parse limit ───────────────────────────────────────────────
            int limit = 50;
            if (args.TryGetProperty("limit", out var limitEl) && limitEl.ValueKind == JsonValueKind.Number)
                limit = Math.Clamp(limitEl.GetInt32(), 1, 500);

            // ── Parse and validate filters ────────────────────────────────
            var filterParams = new Dictionary<string, object?>(StringComparer.Ordinal);

            if (args.TryGetProperty("filters", out var filtersEl) && filtersEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in filtersEl.EnumerateObject())
                {
                    if (!FilterAllowlist.Contains(prop.Name))
                        return ToJsonElement(new { error = "filter_not_allowed", field = prop.Name });

                    filterParams["f_" + prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String  => (object?)prop.Value.GetString(),
                        JsonValueKind.Number  => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                        JsonValueKind.True    => true,
                        JsonValueKind.False   => false,
                        _                     => prop.Value.GetRawText()
                    };
                }
            }

            // ── AvatarId comes exclusively from context ────────────────────
            // (privilege-escalation gate — line 134)
            var avatarIdStr = ToSurrealId(context.AvatarId);

            // ── Build query ───────────────────────────────────────────────
            // Table name is from the allowlist — substituted via SurrealQuery.SelectAll
            // which routes through SurrealIdentifier.ForTable inside the safe layer,
            // so the interpolation never reaches SurrealQuery.Of (SRDB0001 safe).
            // Fluent .Where() / .Limit() append parameterized clauses.
            var q = SurrealQuery.SelectAll(tableName)
                                .Where("avatar_id = $avatar_id", new { avatar_id = avatarIdStr });

            // Append each validated filter as an additional AND clause.
            foreach (var kv in filterParams)
            {
                // k = "f_name" → field name "name"; SQL ref "$f_name"
                var fieldName = kv.Key.Substring(2);
                q = q.Where(fieldName + " = $" + kv.Key).WithParam(kv.Key, kv.Value);
            }

            q = q.Limit(limit);

            var rows = await context.Executor.ExecuteAsync(q, ct);
            rows.EnsureAllOk();

            // GetValues returns JsonElement-level rows; deserialize as JsonElement list.
            var rawRows = rows.GetValues<JsonElement>(0);
            var rowList = rawRows.Select(r => r.Clone()).ToList();

            return ToJsonElement(new
            {
                table     = tableName,
                rows      = rowList,
                row_count = rowList.Count
            });
        }
        catch (Exception ex)
        {
            return Error("internal", ex.Message);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ToSurrealId(Guid id) => id.ToString("N").ToLowerInvariant();

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
}
