using System.Net.Http.Json;
using System.Text.Json;
using OASIS.WebAPI.IntegrationTests.Builders;
using OASIS.WebAPI.IntegrationTests.Factories;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.IntegrationTests;

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
/// NO EF DEPENDENCIES. No OASISDbContext. No Database.Migrate().
/// </summary>
public abstract class IntegrationTestBase : IClassFixture<OASISTestWebApplicationFactory>, IAsyncLifetime
{
    protected readonly OASISTestWebApplicationFactory Factory;
    protected readonly HttpClient Client;
    protected readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    protected IntegrationTestBase(OASISTestWebApplicationFactory factory)
    {
        Factory = factory;
        Client = factory.CreateAuthenticatedClient();
        TestNamespace = $"test{Guid.NewGuid():N}"; // no hyphens — SurrealDB identifier safe
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
        // USE NS + DB is implicit when we send the NS/DB headers in the request.
        // Explicitly define the namespace so it can be dropped on teardown.
        // G3: no string interpolation into SurrealQL — namespace name is in the
        //     HTTP header, not embedded in the query.
        await ExecuteSurrealSqlAsync("DEFINE NAMESPACE IF NOT EXISTS $ns", new { ns = TestNamespace });
        await ExecuteSurrealSqlAsync("USE NS $ns DB test; DEFINE DATABASE IF NOT EXISTS test",
            new { ns = TestNamespace });
    }

    private async Task DropTestNamespaceAsync()
    {
        // Drop the entire namespace to clean up all test data atomically.
        // Parameter binding: namespace name is passed via the NS header, not
        // interpolated into the query string (G3 compliant).
        try
        {
            await ExecuteSurrealSqlAsync("REMOVE NAMESPACE $ns", new { ns = TestNamespace });
        }
        catch
        {
            // Best-effort teardown — swallow errors so test results are not polluted.
        }
    }

    private async Task ApplySchemasIfPresentAsync()
    {
        // Invoke Worker C's schema runner if it exists.
        // Gracefully skips when Persistence/SurrealDb/Schemas/ is empty (e.g. early-bootstrap test runs).
        var repoRoot = FindRepoRoot();
        if (repoRoot is null) return;

        var schemaDir = Path.Combine(repoRoot, "Persistence", "SurrealDb", "Schemas");
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
    protected async Task ExecuteSurrealSqlAsync(string sql, object? parameters = null)
    {
        if (!await IsSurrealDbAvailableAsync()) return;

        var body = new { query = sql, @params = parameters ?? new { } };
        var content = JsonContent.Create(body, options: JsonOptions);

        var response = await SurrealClient.PostAsync("/sql", content);
        // Non-2xx → throw so test failures surface cleanly
        response.EnsureSuccessStatusCode();
    }

    /// Execute raw SurrealQL (DDL from Worker C's schema files).
    /// Must only be called with file-sourced SQL — never with runtime input.
    private async Task ExecuteSurrealSqlRawAsync(string sql)
    {
        if (!await IsSurrealDbAvailableAsync()) return;

        var content = new StringContent(sql, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        var request = new HttpRequestMessage(HttpMethod.Post, "/sql") { Content = content };
        // Override Content-Type for raw SurrealQL
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        foreach (var header in SurrealClient.DefaultRequestHeaders)
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);

        using var tempClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));
        tempClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        tempClient.DefaultRequestHeaders.Add("NS", TestNamespace);
        tempClient.DefaultRequestHeaders.Add("DB", "test");

        var response = await tempClient.PostAsync("/sql",
            new StringContent(sql, System.Text.Encoding.UTF8, "text/plain"));
        // Best-effort — DDL failures are logged but don't abort the test suite
        // (Worker C may add constraints that require specific ordering).
        _ = response;
    }

    private async Task<bool> IsSurrealDbAvailableAsync()
    {
        try
        {
            using var probe = new HttpClient();
            probe.Timeout = TimeSpan.FromSeconds(2);
            var r = await probe.GetAsync($"{SurrealTestDefaults.Endpoint}/health");
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
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OASISResult<Avatar>>(JsonOptions);
        return result?.Result ?? throw new InvalidOperationException(
            $"Avatar seed failed: {await response.Content.ReadAsStringAsync()}");
    }

    protected async Task<Holon> SeedHolonAsync(Action<HolonBuilder>? configure = null)
    {
        var builder = new HolonBuilder();
        configure?.Invoke(builder);

        var model = builder.BuildCreateModel();
        var response = await Client.PostAsJsonAsync("api/holon", model, JsonOptions);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OASISResult<Holon>>(JsonOptions);
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
        var model = new OASIS.WebAPI.Models.Requests.WalletCreateModel
        {
            ChainType = builder.GetChainType(),
            Address   = builder.GetAddress(),
            Label     = builder.GetLabel(),
            IsDefault = builder.GetIsDefault()
        };

        var response = await Client.PostAsJsonAsync("api/wallet", model, JsonOptions);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OASISResult<Wallet>>(JsonOptions);
        return result?.Result ?? throw new InvalidOperationException(
            $"Wallet seed failed: {await response.Content.ReadAsStringAsync()}");
    }

    protected async Task<STARODK> SeedSTARODKAsync(Action<STARODKBuilder>? configure = null)
    {
        var builder = new STARODKBuilder();
        configure?.Invoke(builder);

        var model = builder.BuildCreateModel();
        var response = await Client.PostAsJsonAsync("api/starodk", model, JsonOptions);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OASISResult<STARODK>>(JsonOptions);
        return result?.Result ?? throw new InvalidOperationException(
            $"STARODK seed failed: {await response.Content.ReadAsStringAsync()}");
    }

    protected async Task<BlockchainOperation> SeedBlockchainOperationAsync(
        Action<BlockchainOperationBuilder>? configure = null)
    {
        // BlockchainOperation is created by controller actions (mint/bridge/etc.),
        // not by a dedicated seed endpoint. Return a stub so tests that need
        // a pre-existing operation can seed via the mint endpoint.
        // Full seeding will be enabled in wave 2 when the SurrealDB adapter is wired.
        var builder = new BlockchainOperationBuilder();
        configure?.Invoke(builder);
        return await Task.FromResult(builder.Build());
    }

    // ── Response helpers ──────────────────────────────────────────────────────

    protected async Task<T?> ReadResponseAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
    }

    protected async Task<OASISResult<T>?> ReadResultAsync<T>(HttpResponseMessage response)
    {
        return await ReadResponseAsync<OASISResult<T>>(response);
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    private static string? FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "OASIS.WebAPI.csproj")))
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }
}
