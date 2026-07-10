using System.Diagnostics;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.IntegrationTests.Builders;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Sagas;

namespace AZOA.WebAPI.IntegrationTests.Perf;

// Run these tests with: dotnet test --filter "Category=Perf"
// Default CI pipeline uses --filter "Category!=Perf" which excludes this class.
[Trait("Category", "Perf")]
public sealed class SurrealPerfBudgets : IAsyncLifetime
{
    // ── Connection config ─────────────────────────────────────────────────────

    // Connection config sourced from SurrealTestDefaults (points at local instance).

    // ── Per-instance state ────────────────────────────────────────────────────

    private readonly string _testNamespace = $"test{Guid.NewGuid():N}";
    private HttpSurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealWalletStore _walletStore = null!;
    private SurrealBridgeStore _bridgeStore = null!;
    private SurrealSagaStore _sagaStore = null!;
    private bool _surrealAvailable;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        _surrealAvailable = await ProbeSurrealAsync();
        if (!_surrealAvailable) return;

        var options = new SurrealConnectionOptions
        {
            Endpoint  = SurrealTestDefaults.Endpoint,
            Namespace = _testNamespace,
            Database  = "test",
            User      = SurrealTestDefaults.User,
            Password  = SurrealTestDefaults.Password
        };

        var http = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        _connection = new HttpSurrealConnection(http, options);
        _executor   = new DefaultSurrealExecutor(_connection);

        _walletStore = new SurrealWalletStore(_executor);
        _bridgeStore = new SurrealBridgeStore(_executor);
        _sagaStore   = new SurrealSagaStore(_executor);

        await BootstrapSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (!_surrealAvailable || _connection is null) return;
        try { await DropNamespaceAsync(); }
        catch { /* best-effort */ }
        finally { _connection.Dispose(); }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task WalletGetById_P99_Under50ms()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container unavailable.");

        var avatarId = Guid.NewGuid();
        var wallets  = new List<Wallet>(100);
        for (int i = 0; i < 100; i++)
        {
            var w = new WalletBuilder()
                .ForAvatar(avatarId)
                .OnChain("Algorand")
                .WithAddress($"perf_{Guid.NewGuid():N}")
                .Build();
            await _walletStore.UpsertAsync(w);
            wallets.Add(w);
        }

        var durations = new List<double>(100);
        for (int i = 0; i < 100; i++)
        {
            var id    = wallets[i % wallets.Count].Id;
            var start = Stopwatch.GetTimestamp();
            await _walletStore.GetByIdAsync(id);
            durations.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        var p99 = Percentile(durations, 99);
        p99.Should().BeLessThan(50, "GetById p99 budget");
    }

    [SkippableFact]
    public async Task BridgeTxInsert_P99_Under100ms()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container unavailable.");

        var avatarId  = Guid.NewGuid();
        var durations = new List<double>(100);

        for (int i = 0; i < 100; i++)
        {
            var tx = new BridgeTransactionResult
            {
                Id            = Guid.NewGuid().ToString("N"),
                AvatarId      = avatarId,
                SourceChain   = "Algorand",
                TargetChain   = "Solana",
                SourceTokenId = "ALGO",
                SourceAddress = $"src_{Guid.NewGuid():N}",
                TargetAddress = $"dst_{Guid.NewGuid():N}",
                Amount        = 100,
                Status        = BridgeStatus.Initiated,
                Mode          = BridgeMode.Trusted,
                CreatedAt     = DateTime.UtcNow,
            };

            var start = Stopwatch.GetTimestamp();
            await _bridgeStore.AddBridgeAsync(tx);
            durations.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        var p99 = Percentile(durations, 99);
        p99.Should().BeLessThan(100, "BridgeTx insert p99 budget");
    }

    [SkippableFact]
    public async Task SagaSteps_DueScan_P99_Under200ms()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container unavailable.");

        var correlationKey = Guid.NewGuid().ToString("N");
        for (int i = 0; i < 200; i++)
        {
            await _sagaStore.EnqueueAsync(
                sagaName:           "PerfTestSaga",
                stepName:           $"Step{i}",
                correlationKey:     correlationKey,
                stepIdempotencyKey: Guid.NewGuid().ToString("N"),
                payloadJson:        "{}",
                isCompensation:     false,
                ct:                 CancellationToken.None);
        }

        var durations    = new List<double>(100);
        var leaseTimeout = TimeSpan.FromMinutes(5);

        for (int i = 0; i < 100; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await _sagaStore.GetDueStepIdsAsync(DateTime.UtcNow, batch: 50, leaseTimeout, CancellationToken.None);
            durations.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        var p99 = Percentile(durations, 99);
        p99.Should().BeLessThan(200, "SagaSteps due-scan p99 budget");
    }

    // ── Percentile helper ─────────────────────────────────────────────────────

    private static double Percentile(IReadOnlyList<double> samples, int percentile)
    {
        if (samples.Count == 0) throw new ArgumentException("samples empty", nameof(samples));
        var sorted = samples.OrderBy(x => x).ToArray();
        var rank   = (percentile / 100.0) * (sorted.Length - 1);
        var lower  = (int)Math.Floor(rank);
        var upper  = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];
        return sorted[lower] + (rank - lower) * (sorted[upper] - sorted[lower]);
    }

    // ── Infrastructure ────────────────────────────────────────────────────────

    private static async Task<bool> ProbeSurrealAsync()
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var r = await probe.GetAsync($"{SurrealTestDefaults.Endpoint}/health");
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task BootstrapSchemaAsync()
    {
        await ExecuteRootSqlAsync($"DEFINE NAMESPACE IF NOT EXISTS {_testNamespace}");
        await ExecuteScopedSqlAsync("DEFINE DATABASE IF NOT EXISTS test");

        var repoRoot = FindRepoRoot();
        if (repoRoot is null) return;

        var schemaDir = Path.Combine(repoRoot, "Persistence", "SurrealDb", "Generated", "Schemas");
        if (!Directory.Exists(schemaDir)) return;

        foreach (var file in Directory.GetFiles(schemaDir, "*.surql").OrderBy(f => f))
            await ExecuteScopedSqlAsync(await File.ReadAllTextAsync(file));
    }

    private async Task DropNamespaceAsync()
    {
        await ExecuteRootSqlAsync($"REMOVE NAMESPACE IF EXISTS {_testNamespace}");
    }

    private static async Task ExecuteRootSqlAsync(string sql)
    {
        using var client = RootClient();
        var response = await client.PostAsync("/sql",
            new StringContent(sql, System.Text.Encoding.UTF8, "text/plain"));
        response.EnsureSuccessStatusCode();
        await EnsureSurrealOkAsync(response);
    }

    private async Task ExecuteScopedSqlAsync(string sql)
    {
        using var client = RootClient();
        client.DefaultRequestHeaders.Add("Surreal-NS", _testNamespace);
        client.DefaultRequestHeaders.Add("Surreal-DB", "test");
        var response = await client.PostAsync("/sql",
            new StringContent(sql, System.Text.Encoding.UTF8, "text/plain"));
        response.EnsureSuccessStatusCode();
        await EnsureSurrealOkAsync(response);
    }

    private static HttpClient RootClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        return client;
    }

    private static async Task EnsureSurrealOkAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        if (payload.Contains("\"status\":\"ERR\"", StringComparison.Ordinal))
            throw new InvalidOperationException($"SurrealQL statement failed: {payload}");
    }

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
