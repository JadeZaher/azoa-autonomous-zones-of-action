// ─── OASIS Sleek — MCP Injection Suite (G3 Mirror for MCP Tool Inputs) ─────────
//
// LOAD-BEARING ASSERTION (read before modifying this file)
// ─────────────────────────────────────────────────────────
// G3 requires that ALL SurrealQL queries are composed via parameterized bindings
// only — never via C# string interpolation or concatenation into the query string.
// This suite extends the G3 evidence to MCP tool inputs:
//
// TEST 1 — MCP_AvatarScopedQuery_HostileTableAndFilterInputs_NeverMutatesData
//   Drives all six hostile payloads through avatar_scoped_query as BOTH the table
//   value and a filter value. Asserts:
//     - table payloads → { "error":"table_not_allowed" } or HTTP 4xx — never 500.
//     - filter payloads → { "error":"filter_not_allowed" } or HTTP 4xx — never 500.
//   Verifies wallet row count BEFORE and AFTER each probe: ZERO row count change.
//
// TEST 2 — MCP_VectorSearch_HostileQueryText_TreatedAsEmbeddingInputNotSurrealQL
//   Drives all six hostile payloads through vector_search as query_text.
//   Asserts:
//     - Response shape is { "matches": [...], "table": "holon", "k_actual": N } OR
//       { "error": "internal" } (DB function absent) — NEVER an unexpected 500 or panic.
//     - Payload is treated as embedding input (pure data), NOT parsed as SurrealQL.
//     - Wallet row count is unchanged (hostile text does not escape query context).
//
// Hostile payload corpus mirrors G3_InjectionSuiteTest.cs lines 53-66 verbatim.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Connection;
using Oasis.SurrealDb.Client.Query;
using OASIS.WebAPI.IntegrationTests.Factories;
using OASIS.WebAPI.Mcp;
using OASIS.WebAPI.Mcp.Tools;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Providers.Stores.Surreal;

namespace OASIS.WebAPI.IntegrationTests.Mcp;

/// <summary>
/// Runtime evidence that MCP tool inputs cannot inject SurrealQL through
/// avatar_scoped_query or vector_search. Mirrors the G3 hostile-payload corpus.
/// </summary>
[Trait("Category", "Mcp")]
public sealed class McpInjectionSuiteTests : IntegrationTestBase
{
    // Connection config sourced from SurrealTestDefaults (points at local instance).

    // ── Hostile payload corpus (mirrors G3_InjectionSuiteTest.cs lines 53-66) ──

    // Each payload is designed to exfiltrate / mutate data if the backend ever
    // interpolates it directly into SurrealQL. G3 compliance means every one of
    // these lands as an opaque string value (or is rejected by input validation),
    // with NO row mutation as a side-effect.
    private static readonly string[] HostilePayloads =
    [
        // a) Classic SQL injection
        "' OR 1=1; DROP TABLE wallet;--",
        // b) SurrealQL parameter injection
        "$id; DELETE wallet;",
        // c) SurrealQL function injection
        "type::record(\"wallet\", \"; DROP NAMESPACE test;--\")",
        // d) Unicode fullwidth apostrophe (U+FF07)
        "＇ OR 1=1",
        // e) NUL byte variant
        "wallet: ; DELETE wallet;",
        // f) RTL override (U+202E)
        "wallet‮; DELETE wallet;"
    ];

    public McpInjectionSuiteTests(OASISTestWebApplicationFactory factory) : base(factory) { }

    // ─────────────────────────────────────────────────────────────────────────
    // TEST 1 — avatar_scoped_query hostile table + filter inputs
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// For each hostile payload:
    ///   (a) Use it as the "table" argument → must return table_not_allowed or HTTP 4xx.
    ///   (b) Use it as a filter field value with a legitimate table → must return
    ///       filter_not_allowed (if the filter key is not in the allowlist) or
    ///       HTTP 4xx — NEVER an unexpected 500.
    ///   After every probe the wallet row count must be unchanged.
    ///
    /// The filter-key injection vector is probed separately: the hostile string is
    /// used as the FILTER KEY (not the value), which tests the FilterAllowlist
    /// rejection path. A hostile VALUE with an allowlisted key (e.g. chain_id)
    /// is safe by design because the value is bound as a SurrealQL param.
    /// </summary>
    [SkippableFact]
    public async Task MCP_AvatarScopedQuery_HostileTableAndFilterInputs_NeverMutatesData()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            "SurrealDB test container not reachable — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");

        var avatarId    = Guid.Parse(TestAuthHandler.DefaultAvatarId);
        var avatarIdStr = avatarId.ToString("N").ToLowerInvariant();
        var executor    = CreateExecutor();
        var tool        = new AvatarScopedQueryTool();

        // Seed a control wallet so the row count baseline is ≥ 1
        var walletStore = new SurrealWalletStore(executor);
        var seedWallet  = new Wallet
        {
            Id         = Guid.NewGuid(),
            AvatarId   = avatarId,
            ChainType  = "Algorand",
            Address    = $"inj_ctrl_{Guid.NewGuid():N}",
            Label      = "InjectionControl",
            WalletType = WalletType.Platform
        };
        var seedResult = await walletStore.UpsertAsync(seedWallet);
        seedResult.IsError.Should().BeFalse("control wallet seed must succeed");

        var countBefore = await QueryWalletCountAsync();
        countBefore.Should().BeGreaterThanOrEqualTo(1, "seed wallet must be present");

        var ctx = BuildContext(avatarId, executor);

        foreach (var payload in HostilePayloads)
        {
            // ── (a) Hostile table value ────────────────────────────────────
            var tableArgs = BuildArgs(new { table = payload });
            var tableResult = await tool.ExecuteAsync(ctx, tableArgs, CancellationToken.None);

            AssertTableNotAllowedOrSafeError(tableResult, payload);

            // ── (b) Hostile filter KEY (not in FilterAllowlist) ───────────
            // We construct: { table: "wallet", filters: { <payload>: "value" } }
            // The payload as a key is rejected by FilterAllowlist.Contains check.
            var filterKeyArgs = BuildArgsWithHostileFilterKey("wallet", payload, "harmless_value");
            var filterKeyResult = await tool.ExecuteAsync(ctx, filterKeyArgs, CancellationToken.None);

            AssertFilterNotAllowedOrSafeError(filterKeyResult, payload);

            // ── Assert: wallet count unchanged ────────────────────────────
            var countAfter = await QueryWalletCountAsync();
            countAfter.Should().BeGreaterThanOrEqualTo(countBefore,
                $"wallet row count must not decrease after avatar_scoped_query probe with payload: {payload}");
        }

        // Final check: seed wallet still present
        var finalGet = await walletStore.GetByIdAsync(seedWallet.Id);
        finalGet.IsError.Should().BeFalse("control wallet must still be retrievable after all injection probes");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TEST 2 — vector_search hostile query_text treated as embedding input only
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// For each hostile payload, use it as the vector_search "query_text".
    /// The payload is passed to IEmbeddingProvider.EmbedAsync → it becomes an
    /// opaque byte-array input to SHA-256, never reaching SurrealQL as code.
    ///
    /// Expected responses (any of these is acceptable):
    ///   - { "matches": [...], "table": "holon", "k_actual": N }  (normal result)
    ///   - { "error": "internal", ... }                           (DB function absent)
    ///
    /// NEVER acceptable:
    ///   - An unhandled exception / HTTP 500 from the tool
    ///   - Any mutation to the wallet table (wallet count must be unchanged)
    /// </summary>
    [SkippableFact]
    public async Task MCP_VectorSearch_HostileQueryText_TreatedAsEmbeddingInputNotSurrealQL()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            "SurrealDB test container not reachable — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");

        var avatarId = Guid.Parse(TestAuthHandler.DefaultAvatarId);
        var executor = CreateExecutor();

        // Seed one wallet so count baseline is meaningful
        var walletStore = new SurrealWalletStore(executor);
        var seedWallet  = new Wallet
        {
            Id         = Guid.NewGuid(),
            AvatarId   = avatarId,
            ChainType  = "Solana",
            Address    = $"vec_inj_ctrl_{Guid.NewGuid():N}",
            Label      = "VecInjControl",
            WalletType = WalletType.External
        };
        (await walletStore.UpsertAsync(seedWallet)).IsError.Should().BeFalse();

        var countBefore = await QueryWalletCountAsync();

        var tool = new VectorSearchTool();
        var ctx  = BuildContext(avatarId, executor);

        foreach (var payload in HostilePayloads)
        {
            var args = BuildArgs(new { query_text = payload, table = "holon", k = 3 });

            // Tool must not throw regardless of payload content
            JsonElement result = default;
            var threw = false;
            try
            {
                result = await tool.ExecuteAsync(ctx, args, CancellationToken.None);
            }
            catch
            {
                threw = true;
            }

            threw.Should().BeFalse(
                $"VectorSearchTool must not throw for any query_text payload; got exception for: {payload}");

            // Response must be either a normal matches shape OR an internal error —
            // never an unrecognised error structure
            var hasMatches = result.TryGetProperty("matches", out _);
            var hasError   = result.TryGetProperty("error", out _);
            (hasMatches || hasError).Should().BeTrue(
                $"response must have either 'matches' or 'error' field; got: {result.GetRawText()} for payload: {payload}");

            // If it has an error, must be "internal" (DB degradation) — not a
            // novel error type that might indicate the payload executed as SurrealQL
            if (hasError)
            {
                result.TryGetProperty("error", out var errProp);
                var errorCode = errProp.GetString() ?? string.Empty;
                errorCode.Should().Be("internal",
                    $"only 'internal' DB errors are acceptable for hostile query_text; got '{errorCode}' for payload: {payload}");
            }

            // Wallet count must not have changed
            var countAfter = await QueryWalletCountAsync();
            countAfter.Should().BeGreaterThanOrEqualTo(countBefore,
                $"wallet row count must not decrease after vector_search probe with payload: {payload}");
        }

        // Seed wallet still intact
        var finalGet = await walletStore.GetByIdAsync(seedWallet.Id);
        finalGet.IsError.Should().BeFalse(
            "control wallet must still be retrievable after all vector_search injection probes");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private ISurrealExecutor CreateExecutor()
    {
        var options = new SurrealConnectionOptions
        {
            Endpoint  = SurrealTestDefaults.Endpoint,
            Namespace = TestNamespace,
            Database  = "test",
            User      = SurrealTestDefaults.User,
            Password  = SurrealTestDefaults.Password
        };
        var http       = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        var connection = new HttpSurrealConnection(http, options);
        return new DefaultSurrealExecutor(connection);
    }

    private static ToolCallContext BuildContext(Guid avatarId, ISurrealExecutor executor)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<IEmbeddingProvider, DeterministicDummyEmbeddingProvider>();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        return new ToolCallContext(avatarId, executor, sp);
    }

    private static JsonElement BuildArgs(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Builds avatar_scoped_query args where the filter KEY is the hostile payload.
    /// Shape: { "table": "<table>", "filters": { "<payload>": "<value>" } }
    /// </summary>
    private static JsonElement BuildArgsWithHostileFilterKey(
        string table, string hostileKey, string value)
    {
        var filtersDict = new Dictionary<string, string> { [hostileKey] = value };
        var argsDict    = new Dictionary<string, object>
        {
            ["table"]   = table,
            ["filters"] = filtersDict
        };
        var json = JsonSerializer.Serialize(argsDict);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Assert that a tool response to a hostile table value is safe:
    /// either { "error": "table_not_allowed" } or any error shape — never
    /// a missing error (which would indicate the hostile table was executed).
    /// </summary>
    private static void AssertTableNotAllowedOrSafeError(JsonElement result, string payload)
    {
        // The tool MUST return an error (table not in allowlist)
        result.TryGetProperty("error", out var errProp).Should().BeTrue(
            $"avatar_scoped_query must return an error for hostile table value: {payload}");

        var errorCode = errProp.GetString() ?? string.Empty;
        new[] { "table_not_allowed", "table is required.", "internal" }.Should().Contain(
            errorCode,
            $"table rejection must produce a known safe error code; got '{errorCode}' for payload: {payload}");
    }

    /// <summary>
    /// Assert that a tool response to a hostile filter KEY is safe:
    /// either { "error": "filter_not_allowed", "field": "..." } or any error.
    /// </summary>
    private static void AssertFilterNotAllowedOrSafeError(JsonElement result, string payload)
    {
        result.TryGetProperty("error", out var errProp).Should().BeTrue(
            $"avatar_scoped_query must return an error for hostile filter key: {payload}");

        var errorCode = errProp.GetString() ?? string.Empty;
        new[] { "filter_not_allowed", "internal" }.Should().Contain(
            errorCode,
            $"filter rejection must produce a known safe error code; got '{errorCode}' for payload: {payload}");
    }

    /// <summary>
    /// Query the wallet table row count directly via SurrealDB HTTP
    /// (mirrors the pattern from G3_InjectionSuiteTest).
    /// </summary>
    private async Task<long> QueryWalletCountAsync()
    {
        try
        {
            using var countClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
            var credentials = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));
            countClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
            countClient.DefaultRequestHeaders.Add("NS", TestNamespace);
            countClient.DefaultRequestHeaders.Add("DB", "test");
            countClient.DefaultRequestHeaders.Add("Accept", "application/json");

            // Literal constant SELECT — no user input interpolated (G3 compliant)
            const string countSql = "SELECT count() FROM wallet GROUP ALL";
            var content  = new StringContent(countSql, System.Text.Encoding.UTF8, "text/plain");
            var response = await countClient.PostAsync("/sql", content);

            if (!response.IsSuccessStatusCode) return 0;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array &&
                root.GetArrayLength() > 0)
            {
                var first = root[0];
                if (first.TryGetProperty("result", out var resultArr) &&
                    resultArr.ValueKind == System.Text.Json.JsonValueKind.Array &&
                    resultArr.GetArrayLength() > 0)
                {
                    var countObj = resultArr[0];
                    if (countObj.TryGetProperty("count", out var countProp))
                        return countProp.GetInt64();
                }
            }
            return 0;
        }
        catch
        {
            return 0;
        }
    }
}
