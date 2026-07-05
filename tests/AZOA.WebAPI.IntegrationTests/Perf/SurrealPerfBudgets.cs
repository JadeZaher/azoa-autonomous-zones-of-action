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
        using var ddlClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));
        ddlClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        ddlClient.DefaultRequestHeaders.Add("NS", _testNamespace);
        ddlClient.DefaultRequestHeaders.Add("DB", "test");

        const string ddl = """
            DEFINE NAMESPACE IF NOT EXISTS $ns;
            USE NS $ns DB test;
            DEFINE DATABASE IF NOT EXISTS test;

            -- wallet (010)
            DEFINE TABLE IF NOT EXISTS wallet SCHEMAFULL;
            DEFINE FIELD IF NOT EXISTS id                    ON wallet TYPE string;
            DEFINE FIELD IF NOT EXISTS avatar_id             ON wallet TYPE string;
            DEFINE FIELD IF NOT EXISTS chain_type            ON wallet TYPE string;
            DEFINE FIELD IF NOT EXISTS address               ON wallet TYPE string;
            DEFINE FIELD IF NOT EXISTS public_key            ON wallet TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS label                 ON wallet TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS is_default            ON wallet TYPE bool DEFAULT false;
            DEFINE FIELD IF NOT EXISTS wallet_type           ON wallet TYPE string;
            DEFINE FIELD IF NOT EXISTS encrypted_private_key ON wallet TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS encrypted_seed_phrase ON wallet TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS created_date          ON wallet TYPE datetime;

            -- bridge_tx (020)
            DEFINE TABLE IF NOT EXISTS bridge_tx SCHEMAFULL;
            DEFINE FIELD IF NOT EXISTS id               ON bridge_tx TYPE string;
            DEFINE FIELD IF NOT EXISTS avatar_id        ON bridge_tx TYPE string;
            DEFINE FIELD IF NOT EXISTS source_chain     ON bridge_tx TYPE string;
            DEFINE FIELD IF NOT EXISTS target_chain     ON bridge_tx TYPE string;
            DEFINE FIELD IF NOT EXISTS source_token_id  ON bridge_tx TYPE string;
            DEFINE FIELD IF NOT EXISTS target_token_id  ON bridge_tx TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS source_address   ON bridge_tx TYPE string;
            DEFINE FIELD IF NOT EXISTS target_address   ON bridge_tx TYPE string;
            DEFINE FIELD IF NOT EXISTS amount           ON bridge_tx TYPE string;
            DEFINE FIELD IF NOT EXISTS status           ON bridge_tx TYPE string;
            DEFINE FIELD IF NOT EXISTS mode             ON bridge_tx TYPE string DEFAULT "Trusted";
            DEFINE FIELD IF NOT EXISTS lock_tx_hash     ON bridge_tx TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS mint_tx_hash     ON bridge_tx TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS proof_data       ON bridge_tx TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS error_message    ON bridge_tx TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS created_at       ON bridge_tx TYPE datetime;
            DEFINE FIELD IF NOT EXISTS completed_at     ON bridge_tx TYPE option<datetime>;
            DEFINE FIELD IF NOT EXISTS wormhole_emitter_chain_id ON bridge_tx TYPE option<int>;
            DEFINE FIELD IF NOT EXISTS wormhole_emitter_address  ON bridge_tx TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS wormhole_sequence         ON bridge_tx TYPE option<int>;
            DEFINE FIELD IF NOT EXISTS vaa_bytes                 ON bridge_tx TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS vaa_signature_count       ON bridge_tx TYPE option<int>;
            DEFINE FIELD IF NOT EXISTS redemption_tx_hash        ON bridge_tx TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS idempotency_key           ON bridge_tx TYPE option<string>;

            -- saga_steps (080)
            DEFINE TABLE IF NOT EXISTS saga_steps SCHEMAFULL;
            DEFINE FIELD IF NOT EXISTS id                   ON saga_steps TYPE string;
            DEFINE FIELD IF NOT EXISTS correlation_key      ON saga_steps TYPE string;
            DEFINE FIELD IF NOT EXISTS saga_name            ON saga_steps TYPE string;
            DEFINE FIELD IF NOT EXISTS step_name            ON saga_steps TYPE string;
            DEFINE FIELD IF NOT EXISTS step_idempotency_key ON saga_steps TYPE string;
            DEFINE FIELD IF NOT EXISTS payload              ON saga_steps TYPE string;
            DEFINE FIELD IF NOT EXISTS status               ON saga_steps TYPE string DEFAULT "Pending";
            DEFINE FIELD IF NOT EXISTS is_compensation      ON saga_steps TYPE bool DEFAULT false;
            DEFINE FIELD IF NOT EXISTS attempt_count        ON saga_steps TYPE int DEFAULT 0;
            DEFINE FIELD IF NOT EXISTS next_run_at          ON saga_steps TYPE datetime;
            DEFINE FIELD IF NOT EXISTS claimed_at           ON saga_steps TYPE option<datetime>;
            DEFINE FIELD IF NOT EXISTS last_error           ON saga_steps TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS output               ON saga_steps TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS dead_lettered        ON saga_steps TYPE bool DEFAULT false;
            DEFINE FIELD IF NOT EXISTS created_at           ON saga_steps TYPE datetime;
            DEFINE FIELD IF NOT EXISTS updated_at           ON saga_steps TYPE datetime;
            DEFINE INDEX IF NOT EXISTS saga_steps_due_scan ON saga_steps FIELDS status, next_run_at
            """;

        var content = new StringContent(ddl, System.Text.Encoding.UTF8, "text/plain");
        _ = await ddlClient.PostAsync("/sql", content);
    }

    private async Task DropNamespaceAsync()
    {
        using var dropClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));
        dropClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        dropClient.DefaultRequestHeaders.Add("NS", _testNamespace);
        dropClient.DefaultRequestHeaders.Add("DB", "test");

        const string removeSql = "REMOVE NAMESPACE $ns";
        _ = await dropClient.PostAsync("/sql",
            new StringContent(removeSql, System.Text.Encoding.UTF8, "text/plain"));
    }
}
