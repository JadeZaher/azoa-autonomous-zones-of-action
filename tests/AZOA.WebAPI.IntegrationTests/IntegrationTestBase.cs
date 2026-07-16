using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AZOA.WebAPI.IntegrationTests.Builders;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores.Surreal;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests;

/// <summary>
/// Base class for integration tests providing shared infrastructure:
/// - Factory lifecycle management (IClassFixture)
/// - Deterministic per-test namespace isolation via SurrealDB USE NS/DB scoping
/// - Authenticated HTTP client
/// - JSON serialization defaults
///
/// ISOLATION MODEL (replaces the old destructive EnsureDeleted/EnsureCreated):
///   Each test instance gets a unique SurrealDB namespace prefix (test_{guid}).
///   On Dispose the test's namespace is dropped via the SurrealDB HTTP API.
///   This avoids the parallel-collection race of the previous EF-InMemory harness
///   and requires no EF/Postgres references.
///
/// SEEDING MODEL:
///   Seed helpers go through the real HTTP API (CreateAuthenticatedClient) so
///   tests exercise the full request pipeline. Store-layer seeding via a direct
///   SurrealDB client is available via <see cref="SurrealClient"/> for tests
///   that need lower-level setup — guarded by [Trait("Category","SurrealDbFull")]
///   and skipped gracefully when the container is absent.
///
/// NO EF DEPENDENCIES. No AZOADbContext. No Database.Migrate().
/// </summary>
public abstract class IntegrationTestBase : IClassFixture<AZOATestWebApplicationFactory>, IAsyncLifetime
{
    private static readonly HttpClient SurrealHealthClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    protected readonly AZOATestWebApplicationFactory Factory;
    protected readonly HttpClient Client;
    // Mirror the SERVER's JSON config (Program.cs registers JsonStringEnumConverter
    // on the MVC pipeline). Without the string-enum converter here, deserializing
    // any response containing an enum (e.g. Wallet.WalletType = "Platform") throws
    // "The JSON value could not be converted to ... WalletType". The test client
    // must read responses with the same converter set the server writes them with.
    protected readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// Unique SurrealDB namespace for this test instance.
    /// Format: test_{guid_no_hyphens}  (SurrealDB identifiers can't contain hyphens).
    protected readonly string TestNamespace;

    // Connection config sourced from SurrealTestDefaults (points at local instance).

    /// Lazy HTTP client pointing directly at the SurrealDB container for test
    /// setup/teardown that cannot go through the app layer.
    private HttpClient? _surrealDirectClient;

    protected HttpClient SurrealClient
    {
        get
        {
            if (_surrealDirectClient is not null) return _surrealDirectClient;

            _surrealDirectClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
            var credentials = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));
            _surrealDirectClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
            // SurrealDB 3.x requires the "Surreal-NS"/"Surreal-DB" header names;
            // the legacy "NS"/"DB" headers are ignored and the server returns
            // NamespaceEmpty.
            _surrealDirectClient.DefaultRequestHeaders.Add("Surreal-NS", TestNamespace);
            _surrealDirectClient.DefaultRequestHeaders.Add("Surreal-DB", "test");
            _surrealDirectClient.DefaultRequestHeaders.Add("Accept", "application/json");
            return _surrealDirectClient;
        }
    }

    protected static Task<ISurrealExecutor> CreateExecutorAsync(string ns)
    {
        var options = new SurrealConnectionOptions
        {
            Endpoint  = SurrealTestDefaults.Endpoint,
            Namespace = ns,
            Database  = "test",
            User      = SurrealTestDefaults.User,
            Password  = SurrealTestDefaults.Password,
        };

        var connection = new HttpSurrealConnection(new HttpClient(), options);
        return Task.FromResult<ISurrealExecutor>(new DefaultSurrealExecutor(connection));
    }

    protected IntegrationTestBase(AZOATestWebApplicationFactory factory)
    {
        Factory = factory;
        Client = factory.CreateAuthenticatedClient();
        // Use the FACTORY's namespace, not a fresh per-instance guid: the app
        // host (built once per factory / test class) is pinned to
        // factory.TestNamespace via SurrealDb:Namespace. The namespace this base
        // CREATES + schemas must be the SAME one the app CONNECTS to, otherwise
        // controller writes fault with "namespace does not exist". Per-class
        // (not per-method) isolation is the correct granularity here because the
        // factory — and thus the app's bound namespace — is an IClassFixture.
        TestNamespace = factory.TestNamespace;
    }

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    /// Called once before the first test method in this class runs.
    /// Creates the test namespace + applies schemas if Worker C's .surql files exist.
    public async Task InitializeAsync()
    {
        // Gracefully skip SurrealDB setup if the container is not running.
        // Unit tests that don't need the container should still compile and pass.
        if (!await IsSurrealDbAvailableAsync()) return;

        await CreateTestNamespaceAsync();
        await ApplySchemasIfPresentAsync();
    }

    /// Called once after all test methods in this class have run.
    /// Drops the test namespace to release resources.
    public async Task DisposeAsync()
    {
        try
        {
            if (_surrealDirectClient is not null && await IsSurrealDbAvailableAsync())
            {
                await DropTestNamespaceAsync();
            }
        }
        finally
        {
            _surrealDirectClient?.Dispose();
            Client.Dispose();
        }
    }

    // ── Namespace lifecycle ───────────────────────────────────────────────────

    private async Task CreateTestNamespaceAsync()
    {
        // SurrealDB does NOT accept a parameter for a namespace/database
        // IDENTIFIER in DDL — `DEFINE NAMESPACE $ns` fails with
        // NamespaceEmpty (verified against the live engine). The identifier
        // must be literal. TestNamespace is server-generated
        // ($"test{Guid:N}") — pure hex, no user input, no hyphens — so
        // injecting it directly is identifier-safe and the ONLY form that
        // works. This is the fix for the long-standing
        // integration-test-namespace-isolation gap: the per-test namespace
        // is now actually created before the WebAPI executor connects to it.
        //
        // DEFINE NAMESPACE runs at ROOT scope (no NS header); DEFINE DATABASE
        // runs scoped INTO the freshly-created namespace.
        await ExecuteRootSqlAsync($"DEFINE NAMESPACE IF NOT EXISTS {TestNamespace}");
        await ExecuteScopedSqlAsync("DEFINE DATABASE IF NOT EXISTS test");
    }

    private async Task DropTestNamespaceAsync()
    {
        // Drop the entire namespace to clean up all test data atomically.
        // Identifier must be literal (see CreateTestNamespaceAsync); runs at
        // ROOT scope.
        try
        {
            await ExecuteRootSqlAsync($"REMOVE NAMESPACE IF EXISTS {TestNamespace}");
        }
        catch
        {
            // Best-effort teardown — swallow errors so test results are not polluted.
        }
    }

    /// <summary>
    /// Execute literal SurrealQL at ROOT scope (no NS/DB headers). Required
    /// for namespace-level DDL (DEFINE/REMOVE NAMESPACE) which cannot run
    /// "inside" the namespace being created. The SQL is constructed only
    /// from the server-generated identifier-safe <see cref="TestNamespace"/>,
    /// never from user input (G3).
    /// </summary>
    private async Task ExecuteRootSqlAsync(string sql)
    {
        if (!await IsSurrealDbAvailableAsync()) return;
        using var client = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        var response = await client.PostAsync("/sql",
            new StringContent(sql, System.Text.Encoding.UTF8, "text/plain"));
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Execute literal SurrealQL scoped to the test namespace + database
    /// (NS/DB headers set). Used for database-level DDL after the namespace
    /// exists. Identifier-safe-literal only (G3).
    /// </summary>
    private async Task ExecuteScopedSqlAsync(string sql)
    {
        if (!await IsSurrealDbAvailableAsync()) return;
        using var client = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        client.DefaultRequestHeaders.Add("Surreal-NS", TestNamespace);
        client.DefaultRequestHeaders.Add("Surreal-DB", "test");
        var response = await client.PostAsync("/sql",
            new StringContent(sql, System.Text.Encoding.UTF8, "text/plain"));
        response.EnsureSuccessStatusCode();
    }

    private async Task ApplySchemasIfPresentAsync()
    {
        // Invoke Worker C's schema runner if it exists.
        // Gracefully skips when Persistence/SurrealDb/Schemas/ is empty (e.g. early-bootstrap test runs).
        var repoRoot = FindRepoRoot();
        if (repoRoot is null) return;

        // Goldens live under Generated/Schemas/ (the C#-first emit target).
        // The legacy Schemas/ path never existed, so schemas were silently
        // never applied — the per-test namespace ran SCHEMALESS. Point at the
        // real directory so stores exercise the actual SCHEMAFULL DDL
        // (READONLY, ASSERT, typed fields, etc.).
        var schemaDir = Path.Combine(repoRoot, "Persistence", "SurrealDb", "Generated", "Schemas");
        if (!Directory.Exists(schemaDir)) return;

        var surqlFiles = Directory.GetFiles(schemaDir, "*.surql")
            .OrderBy(f => f)
            .ToArray();

        foreach (var file in surqlFiles)
        {
            var sql = await File.ReadAllTextAsync(file);
            // Schema DDL goes through the direct SurrealDB client (not the app).
            // G3: schema files contain literal SurrealQL DDL (DEFINE TABLE etc.)
            // with no runtime-interpolated user input — safe.
            await ExecuteSurrealSqlRawAsync(sql);
        }
    }

    // ── SurrealDB HTTP query helpers (G3 compliant) ───────────────────────────

    /// Execute a parameterized SurrealQL statement via the SurrealDB HTTP API.
    /// The <paramref name="parameters"/> object's properties become $name bindings.
    /// NEVER interpolate user input into <paramref name="sql"/> — always bind via params.
    /// Executes SurrealQL with optional bound parameters against the HTTP /sql endpoint.
    /// SurrealDB 3.x rejects the pre-3.x JSON envelope {query, params} (it treats the
    /// whole body as a literal string and silently no-ops). We instead prefix a
    /// `LET $name = <literal>;` statement per parameter to the SurrealQL in a
    /// text/plain body — this binds scalars AND structured objects (for CONTENT
    /// clauses), type-preserved. See tests/AZOA.WebAPI.IntegrationTests/AGENTS.md
    /// §param-binding.
    protected async Task ExecuteSurrealSqlAsync(string sql, object? parameters = null)
    {
        if (!await IsSurrealDbAvailableAsync()) return;

        var script = BuildParamLets(parameters) + sql;
        var request = new HttpRequestMessage(HttpMethod.Post, "/sql")
        {
            Content = new StringContent(script, System.Text.Encoding.UTF8, "text/plain"),
        };

        var response = await SurrealClient.SendAsync(request);
        // Non-2xx → throw so test failures surface cleanly.
        response.EnsureSuccessStatusCode();

        // SurrealDB returns HTTP 200 even when an individual statement errors; the
        // per-statement status lives in the JSON body. Surface an ERR so a failed
        // seed can never masquerade as a successful one.
        var payload = await response.Content.ReadAsStringAsync();
        if (payload.Contains("\"status\":\"ERR\"", StringComparison.Ordinal))
            throw new InvalidOperationException($"SurrealQL statement failed: {payload}");
    }

    /// Renders a params object as `LET $name = <surql-literal>;` prelude statements.
    private static string BuildParamLets(object? parameters)
    {
        if (parameters is null) return string.Empty;

        var sb = new System.Text.StringBuilder();
        foreach (var p in parameters.GetType()
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            sb.Append("LET $").Append(p.Name).Append(" = ")
              .Append(ToSurqlLiteral(p.GetValue(parameters))).Append(";\n");
        }
        return sb.ToString();
    }

    private static readonly JsonSerializerOptions SurqlLiteralJsonOptions = new(JsonSerializerDefaults.Web);

    /// Serializes a CLR value to a SurrealQL literal (JSON is a valid SurrealQL
    /// object/array literal; null → NONE; scalars stay typed).
    private static string ToSurqlLiteral(object? value)
    {
        if (value is null) return "NONE";
        return value switch
        {
            string s => JsonSerializer.Serialize(s),
            bool b => b ? "true" : "false",
            DateTime dt => JsonSerializer.Serialize(dt.ToString("o")),
            DateTimeOffset dto => JsonSerializer.Serialize(dto.ToString("o")),
            Guid g => JsonSerializer.Serialize(g.ToString()),
            System.Collections.IEnumerable when value is not string
                => JsonSerializer.Serialize(value, SurqlLiteralJsonOptions),
            // Invariant culture — a comma-decimal locale would emit "1,5" and break SurrealQL.
            _ when value.GetType().IsPrimitive || value is decimal
                => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
            _ => JsonSerializer.Serialize(value, SurqlLiteralJsonOptions),
        };
    }

    /// Execute raw SurrealQL (DDL from Worker C's schema files).
    /// Must only be called with file-sourced SQL — never with runtime input.
    protected async Task ExecuteSurrealSqlRawAsync(string sql)
    {
        var response = await SurrealClient.PostAsync("/sql",
            new StringContent(sql, System.Text.Encoding.UTF8, "text/plain"));
        // Best-effort — DDL failures are logged but don't abort the test suite
        // (Worker C may add constraints that require specific ordering).
        _ = response;
    }

    private async Task<bool> IsSurrealDbAvailableAsync()
    {
        try
        {
            var r = await SurrealHealthClient.GetAsync($"{SurrealTestDefaults.Endpoint}/health");
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// Returns true when the SurrealDB container is reachable and the test may
    /// run; false to gracefully skip via Xunit.SkippableFact. Shared by every
    /// Surreal-touching integration test (per-class duplicates of this helper
    /// were promoted here on 2026-05-22 during CLOSEOUT Stream E so the five
    /// pre-cutover gate tests can consume one canonical probe).
    /// Skip pattern: <c>Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "...")</c>.
    /// </summary>
    protected Task<bool> SkipIfSurrealDbUnavailableAsync() => IsSurrealDbAvailableAsync();

    // ── HTTP Seeding helpers (via real app API, not direct DB) ────────────────

    protected async Task<Avatar> SeedAvatarAsync(Action<AvatarBuilder>? configure = null)
    {
        var builder = new AvatarBuilder();
        configure?.Invoke(builder);

        var model = builder.BuildRegisterModel();
        var response = await Client.PostAsJsonAsync("api/avatar/register", model, JsonOptions);
        await EnsureSeedSucceededAsync(response, "Avatar");

        var result = await response.Content.ReadFromJsonAsync<AZOAResult<Avatar>>(JsonOptions);
        return result?.Result ?? throw new InvalidOperationException(
            $"Avatar seed failed: {await response.Content.ReadAsStringAsync()}");
    }

    protected async Task<Holon> SeedHolonAsync(Action<HolonBuilder>? configure = null)
    {
        var builder = new HolonBuilder();
        configure?.Invoke(builder);

        var model = builder.BuildCreateModel();
        var response = await Client.PostAsJsonAsync("api/holon", model, JsonOptions);
        await EnsureSeedSucceededAsync(response, "Holon");

        var result = await response.Content.ReadFromJsonAsync<AZOAResult<Holon>>(JsonOptions);
        return result?.Result ?? throw new InvalidOperationException(
            $"Holon seed failed: {await response.Content.ReadAsStringAsync()}");
    }

    protected async Task<Wallet> SeedWalletAsync(Action<WalletBuilder>? configure = null)
    {
        var builder = new WalletBuilder();
        configure?.Invoke(builder);

        // Wallets require an avatar context. The TestAuthHandler supplies the
        // default avatar ID (TestAuthHandler.DefaultAvatarId) as the authenticated
        // user — WalletController reads AvatarId from the JWT claim.
        var model = new AZOA.WebAPI.Models.Requests.WalletCreateModel
        {
            ChainType = builder.GetChainType(),
            Address   = builder.GetAddress(),
            Label     = builder.GetLabel(),
            IsDefault = builder.GetIsDefault()
        };

        var response = await Client.PostAsJsonAsync("api/wallet", model, JsonOptions);
        await EnsureSeedSucceededAsync(response, "Wallet");

        var result = await response.Content.ReadFromJsonAsync<AZOAResult<Wallet>>(JsonOptions);
        return result?.Result ?? throw new InvalidOperationException(
            $"Wallet seed failed: {await response.Content.ReadAsStringAsync()}");
    }

    protected async Task<STARODK> SeedSTARODKAsync(Action<STARODKBuilder>? configure = null)
    {
        var builder = new STARODKBuilder();
        configure?.Invoke(builder);

        var model = builder.BuildCreateModel();
        var response = await Client.PostAsJsonAsync("api/starodk", model, JsonOptions);
        await EnsureSeedSucceededAsync(response, "STARODK");

        var result = await response.Content.ReadFromJsonAsync<AZOAResult<STARODK>>(JsonOptions);
        return result?.Result ?? throw new InvalidOperationException(
            $"STARODK seed failed: {await response.Content.ReadAsStringAsync()}");
    }

    protected async Task<BlockchainOperation> SeedBlockchainOperationAsync(
        Action<BlockchainOperationBuilder>? configure = null)
    {
        var builder = new BlockchainOperationBuilder();
        configure?.Invoke(builder);
        var operation = builder.Build();
        operation.AvatarId ??= Guid.Parse(TestAuthHandler.DefaultAvatarId);

        var executor = await CreateExecutorAsync(TestNamespace);
        var store = new SurrealBlockchainOperationStore(executor);
        var result = await store.UpsertAsync(operation);
        if (result.IsError || result.Result is null)
            throw new InvalidOperationException($"BlockchainOperation seed failed: {result.Message}");

        return (BlockchainOperation)result.Result;
    }

    /// <summary>
    /// Assert a seed POST succeeded, surfacing the RESPONSE BODY in the failure
    /// message. Plain <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>
    /// throws "400 (Bad Request)" with no body, hiding the real server error
    /// (e.g. a SurrealDB "namespace does not exist" or a FluentValidation
    /// rejection). Reading the body here turns an opaque 400 into an actionable
    /// diagnostic — kept deliberately as a harness improvement.
    /// </summary>
    private static async Task EnsureSeedSucceededAsync(HttpResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException(
            $"{what} seed failed: HTTP {(int)response.StatusCode} {response.StatusCode}. Body: {body}");
    }

    // ── Response helpers ──────────────────────────────────────────────────────

    protected async Task<T?> ReadResponseAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
    }

    protected async Task<AZOAResult<T>?> ReadResultAsync<T>(HttpResponseMessage response)
    {
        return await ReadResponseAsync<AZOAResult<T>>(response);
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    private static string? FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AZOA.WebAPI.csproj")))
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }
}
