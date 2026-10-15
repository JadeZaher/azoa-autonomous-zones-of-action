using System.Diagnostics;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.IntegrationTests.Builders;
using AZOA.WebAPI.IntegrationTests.Persistence.Surreal;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Sagas;
using Xunit.Abstractions;

namespace AZOA.WebAPI.IntegrationTests.Perf;

/// <summary>
/// Store-level latency budgets against a real SurrealDB. Run with
/// <c>dotnet test --filter "Category=Perf"</c>; the default CI test filter is
/// <c>Category!=Chaos&amp;Category!=Perf</c>, so the dedicated <c>perf</c> job in
/// .github/workflows/ci.yml is what actually runs this class.
/// Budget provenance and the ceiling derivation: see Perf/AGENTS.md.
/// </summary>
[Trait("Category", "Perf")]
public sealed class SurrealPerfBudgets : IAsyncLifetime
{
    // -- Budget policy --------------------------------------------------------

    // Declared targets are the aspirational per-operation p99 from the original
    // surrealdb-migration track. They are REPORTED, not asserted: the only real
    // measurement this repo has ever recorded for the first of them is
    // "observed p99 64-132 ms vs 50 ms budget" on an unloaded developer host
    // (conductor/tracks/integration-test-isolation-debt/INTEGRATION-TEST-PASSOFF.md).
    // Asserting a target already known to be exceeded by 2.6x is how a suite gets
    // excluded from CI and then rots -- which is exactly what happened here.
    // What IS asserted is a regression ceiling: target x CeilingMultiplier.
    private const double WalletGetByIdTargetMs = 50;
    private const double BridgeInsertTargetMs  = 100;
    private const double SagaDueScanTargetMs   = 200;

    private const double DefaultCeilingMultiplier = 8;

    // Untimed iterations that absorb JIT, the connection handshake and the first
    // serializer pass. Without them the p99 over 100 samples -- which is
    // effectively the second-worst sample -- is dominated by cold-start cost
    // rather than by query cost.
    private const int WarmupIterations   = 10;
    private const int MeasuredIterations = 100;

    // -- Per-instance state ---------------------------------------------------

    private readonly ITestOutputHelper _output;
    private readonly string _testNamespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealWalletStore _walletStore = null!;
    private SurrealBridgeStore _bridgeStore = null!;
    private SurrealSagaStore _sagaStore = null!;
    private bool _surrealAvailable;

    public SurrealPerfBudgets(ITestOutputHelper output) => _output = output;

    // -- IAsyncLifetime -------------------------------------------------------

    public async Task InitializeAsync()
    {
        _surrealAvailable = await ProbeSurrealAsync();
        if (!_surrealAvailable)
        {
            // A skipped perf suite is indistinguishable from a passing one in the
            // test output, which is how this class spent its whole life reading as
            // coverage. CI sets AZOA_PERF_REQUIRE_SURREAL=1 so a skip there is a
            // hard failure instead of a silent green.
            if (RequireSurreal)
                throw new InvalidOperationException(
                    $"AZOA_PERF_REQUIRE_SURREAL is set, but {SurrealTestDefaults.Endpoint}/health did not " +
                    "answer. The perf budgets must not silently skip in CI -- start SurrealDB or unset the flag.");
            return;
        }

        // Bootstrap through the shared helper rather than hand-rolled DDL. The
        // hand-rolled version set a Surreal-DB header pointing at a database that
        // did not exist yet, and opened a fresh HttpClient per statement -- the
        // documented cause of this class's "namespace 'test<guid>' does not exist"
        // failures (SurrealTestSchema.cs:50-55). The helper also throws on a failed
        // apply, so a tableless run cannot masquerade as a fast one.
        await SurrealTestSchema.BootstrapAsync(_testNamespace, "wallet", "bridge_tx", "saga_steps");

        // Constructed only AFTER the namespace and database exist.
        var options = new SurrealConnectionOptions
        {
            Endpoint  = SurrealTestDefaults.Endpoint,
            Namespace = _testNamespace,
            Database  = "test",
            User      = SurrealTestDefaults.User,
            Password  = SurrealTestDefaults.Password
        };

        _connection = new SurrealDbNetConnection(options);
        _executor   = new DefaultSurrealExecutor(_connection);

        _walletStore = new SurrealWalletStore(_executor);
        _bridgeStore = new SurrealBridgeStore(_executor);
        _sagaStore   = new SurrealSagaStore(_executor);
    }

    public async Task DisposeAsync()
    {
        if (!_surrealAvailable) return;
        try { await SurrealTestSchema.DropAsync(_testNamespace); }
        finally { _connection?.Dispose(); }
    }

    // -- Tests ----------------------------------------------------------------

    [SkippableFact]
    public async Task WalletGetById_MeetsRegressionCeiling()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container unavailable.");

        var userId  = Guid.NewGuid();
        var wallets = new List<Wallet>(MeasuredIterations);
        for (int i = 0; i < MeasuredIterations; i++)
        {
            var wallet = new WalletBuilder().ForUser(userId).OnChain("Algorand").Build();
            var seeded = await _walletStore.UpsertAsync(wallet);
            // SurrealWalletStore captures exceptions into AZOAResult instead of
            // throwing. Unchecked, a store that fails on every call returns in
            // microseconds and passes any latency budget -- the measurement only
            // means something once the work is proven to have happened.
            seeded.IsError.Should().BeFalse($"seeding wallet {i} must succeed, got: {seeded.Message}");
            wallets.Add(wallet);
        }

        for (int i = 0; i < WarmupIterations; i++)
            await _walletStore.GetByIdAsync(wallets[i % wallets.Count].Id);

        var durations = new List<double>(MeasuredIterations);
        var reads     = new List<AZOAResult<IWallet>>(MeasuredIterations);
        for (int i = 0; i < MeasuredIterations; i++)
        {
            var id    = wallets[i % wallets.Count].Id;
            var start = Stopwatch.GetTimestamp();
            var read  = await _walletStore.GetByIdAsync(id);
            durations.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            reads.Add(read);
        }

        reads.Should().OnlyContain(r => !r.IsError && r.Result != null,
            "every measured GetById must have returned a real wallet");
        ReportAndAssert("wallet GetById", durations, WalletGetByIdTargetMs);
    }

    [SkippableFact]
    public async Task BridgeTxInsert_MeetsRegressionCeiling()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container unavailable.");

        var userId = Guid.NewGuid();

        // AddBridgeAsync calls EnsureAllOk(), so a failed insert throws and fails
        // the test on its own -- no post-hoc success assertion is needed here.
        for (int i = 0; i < WarmupIterations; i++)
            await _bridgeStore.AddBridgeAsync(NewBridgeTx(userId));

        var durations = new List<double>(MeasuredIterations);
        for (int i = 0; i < MeasuredIterations; i++)
        {
            var tx    = NewBridgeTx(userId);
            var start = Stopwatch.GetTimestamp();
            await _bridgeStore.AddBridgeAsync(tx);
            durations.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        ReportAndAssert("bridge_tx insert", durations, BridgeInsertTargetMs);
    }

    [SkippableFact]
    public async Task SagaSteps_DueScan_MeetsRegressionCeiling()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container unavailable.");

        // Each scan CLAIMS the rows it returns, so a scan only measures a loaded
        // due-set while pending work remains. scanBatch x (warmup + measured) rows
        // are consumed, so enqueue a margin above that and every measured iteration
        // scans a table that still has claimable work in it. The prior version
        // enqueued 200 and claimed 50 at a time, so iterations 5..100 scanned a
        // fully-claimed table and measured the empty-scan path instead.
        const int scanBatch = 2;
        var enqueueCount = scanBatch * (WarmupIterations + MeasuredIterations) + 20;

        var correlationKey = Guid.NewGuid().ToString("N");
        for (int i = 0; i < enqueueCount; i++)
        {
            // EnqueueAsync calls EnsureAllOk(), so a failed enqueue throws here.
            await _sagaStore.EnqueueAsync(
                sagaName:           "PerfTestSaga",
                stepName:           $"Step{i}",
                correlationKey:     correlationKey,
                stepIdempotencyKey: Guid.NewGuid().ToString("N"),
                payloadJson:        "{}",
                isCompensation:     false,
                ct:                 CancellationToken.None);
        }

        var leaseTimeout = TimeSpan.FromMinutes(5);

        for (int i = 0; i < WarmupIterations; i++)
            await _sagaStore.GetDueStepIdsAsync(DateTime.UtcNow, scanBatch, leaseTimeout, CancellationToken.None);

        var durations   = new List<double>(MeasuredIterations);
        var claimCounts = new List<int>(MeasuredIterations);
        for (int i = 0; i < MeasuredIterations; i++)
        {
            var start = Stopwatch.GetTimestamp();
            var due   = await _sagaStore.GetDueStepIdsAsync(DateTime.UtcNow, scanBatch, leaseTimeout, CancellationToken.None);
            durations.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            claimCounts.Add(due.Count);
        }

        // An empty scan is fast and meaningless. If the schema never applied or the
        // enqueues did not land, every scan returns nothing and the budget passes
        // trivially -- so require that each measured scan actually found work.
        claimCounts.Should().OnlyContain(c => c == scanBatch,
            $"every measured due-scan must claim {scanBatch} steps from the {enqueueCount} enqueued");
        ReportAndAssert("saga_steps due-scan", durations, SagaDueScanTargetMs);
    }

    // -- Reporting + assertion ------------------------------------------------

    /// <summary>Logs the measured distribution, then asserts only the regression ceiling.</summary>
    private void ReportAndAssert(string label, IReadOnlyList<double> durations, double declaredTargetMs)
    {
        var p50     = Percentile(durations, 50);
        var p99     = Percentile(durations, 99);
        var ceiling = declaredTargetMs * CeilingMultiplier;

        _output.WriteLine(
            $"[perf] {label}: p50={p50:F1}ms p99={p99:F1}ms " +
            $"(declared target {declaredTargetMs:F0}ms, asserted ceiling {ceiling:F0}ms, " +
            $"n={durations.Count} after {WarmupIterations} warmup)");

        p99.Should().BeLessThan(ceiling,
            $"{label} p99 must stay under the {CeilingMultiplier}x regression ceiling; " +
            $"measured {p99:F1}ms against a declared target of {declaredTargetMs:F0}ms");
    }

    /// <summary>Multiplier applied to a declared target to get the asserted ceiling.</summary>
    private static double CeilingMultiplier
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("AZOA_PERF_CEILING_MULTIPLIER");
            return double.TryParse(raw, out var parsed) && parsed > 0 ? parsed : DefaultCeilingMultiplier;
        }
    }

    /// <summary>When set, an unreachable SurrealDB is a failure rather than a skip.</summary>
    private static bool RequireSurreal =>
        Environment.GetEnvironmentVariable("AZOA_PERF_REQUIRE_SURREAL") is "1" or "true";

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

    // -- Fixtures -------------------------------------------------------------

    private static BridgeTransactionResult NewBridgeTx(Guid userId) => new()
    {
        Id            = Guid.NewGuid().ToString("N"),
        UserId        = userId,
        SourceChain   = "Algorand",
        TargetChain   = "Solana",
        SourceTokenId = "ALGO",
        SourceAddress = $"src{Guid.NewGuid():N}",
        TargetAddress = $"dst{Guid.NewGuid():N}",
        Amount        = 100,
        Status        = BridgeStatus.Initiated,
        Mode          = BridgeMode.Trusted,
        CreatedAt     = DateTime.UtcNow,
    };

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
}
