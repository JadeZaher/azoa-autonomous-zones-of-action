// ─── AZOA — Guardrail G1: Crash-Durability Gate ───────────────────────
//
// LOAD-BEARING ASSERTION (read before modifying this file)
// ──────────────────────────────────────────────────────────
// G1 requires that every write commit is fsynced to disk before the server
// ACKs the client. Without per-commit fsync, a hard kill of the process
// (SIGKILL / kill -9) may leave unflushed write buffers — some rows inserted
// *before* the kill can silently disappear after restart.
//
// DURABILITY SIGNAL ON SurrealDB 3.x (rocksdb engine).
// The 1.5.x-era `surrealkv://...?sync=every` URI parameter is RETIRED — the
// v3.1.4 cutover (see conductor/tracks/_archive/surrealdb-major-upgrade/
// DECISION.md) keeps the RocksDB engine (`rocksdb:///data/db`) and drives
// per-commit fsync via the `SURREAL_SYNC_DATA: "true"` compose env var.
// RocksDB syncs its WAL on every commit under that flag. surrealkv is a
// deliberately-deferred follow-up, NOT the current durable path.
//
// G1_HardKill_DurableInserts_SurviveRestart proves this at runtime:
//   1. Insert N=20 bridge_tx rows + N=20 saga_steps rows via the real stores.
//   2. Hard-kill the configured SurrealDB container.
//   3. Restart the container and wait for /health.
//   4. Re-query every row by its deterministic id and assert byte-identical fields.
//
// If the container is running WITHOUT per-commit fsync, some rows will be lost
// after the kill and the FluentAssertions equivalence check will FAIL. That
// failure is INTENTIONAL — it proves the deploy-time durability posture is real
// runtime evidence, not just a documentation checkbox.
//
// G1_DurabilityAckGate_FailsClosed_IfSyncEventual is a static config assertion
// that reads docker-compose.dev.yml and asserts the durable posture: the
// rocksdb engine URI + `SURREAL_SYNC_DATA: "true"`. It runs without a live
// container, so a deploy that drops the sync flag fails CI before the first
// container ever starts. (SurrealDB exposes no runtime SQL surface to read the
// fsync mode back — see memory [[surrealdb-fsync-mode-not-introspectable]] —
// so this static gate + the SurrealDb:G1DurabilityAcknowledged boot ack are
// the enforcement points, per Program.cs's boot self-check.)
//
// Both tests are guarded by [Trait("Category","Chaos")] so they are excluded
// from the default CI filter (--filter "Category!=Chaos") and opt-in only.

using System.Diagnostics;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Sagas;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Sagas;

namespace AZOA.WebAPI.IntegrationTests.Gates;

/// <summary>
/// Runtime evidence for AZOA guardrail G1 (crash durability).
/// Demonstrates that rows written via the real SurrealBridgeStore and
/// SurrealSagaStore survive a hard SIGKILL + container restart, proving that
/// the <c>surrealkv://data/azoa.db?sync=every</c> URI parameter is
/// load-bearing and not merely advisory.
/// </summary>
/// <remarks>
/// Chaos test — defaults to the local podman container; CI injects Docker coordinates.
/// </remarks>
[Trait("Category", "Chaos")]
public sealed class G1_CrashDurabilityTest : IntegrationTestBase
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const int N = 20;

    // Deterministic seed Guids — same values used for both INSERT and re-query
    // so the post-restart reads are completely deterministic regardless of
    // in-memory state.  Constructed as a stable sequence: G1-bridge-00..19
    // and G1-saga-00..19.
    private static readonly Guid SeedBase = new("a1a1a1a1-b2b2-c3c3-d4d4-e5e5e5e5e5e5");

    // Connection config sourced from SurrealTestDefaults (points at local instance).

    // ── Constructor (IClassFixture<AZOATestWebApplicationFactory>) ───────────

    public G1_CrashDurabilityTest(AZOATestWebApplicationFactory factory)
        : base(factory)
    {
    }

    // ── Test 1: Hard-kill + restart — every row must survive ─────────────────

    /// <summary>
    /// Inserts N=20 bridge_tx rows and N=20 saga_steps rows, hard-kills the
    /// configured SurrealDB container, restarts it,
    /// waits for the /health probe, then re-queries every row and asserts
    /// byte-identical field values via FluentAssertions BeEquivalentTo.
    ///
    /// FAILS CLOSED when the container lacks <c>sync=every</c>: unflushed
    /// buffers are lost on SIGKILL and the BeEquivalentTo assertions detect
    /// missing rows, surfacing the durability gap as a test failure rather than
    /// a silent data loss incident in production.
    /// </summary>
    [SkippableFact]
    public async Task G1_HardKill_DurableInserts_SurviveRestart()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            $"SurrealDB container not reachable at {SurrealTestDefaults.Endpoint} — start the dev stack via `./dev-up.ps1` (brings up the `azoa-dev-surrealdb` v3.1.4 container).");

        // ── Phase 1: Build real stores against TestNamespace ─────────────────
        // InitializeAsync (IntegrationTestBase) already created the namespace
        // and applied all .surql schemas before this method body runs.
        var executor = CreateExecutor();
        var bridgeStore = new SurrealBridgeStore(executor);
        var sagaStore   = new SurrealSagaStore(executor);

        // ── Phase 2: Insert N=20 bridge_tx rows ───────────────────────────────
        var insertedBridges = new List<BridgeTransactionResult>(N);
        for (var i = 0; i < N; i++)
        {
            var tx = MakeBridgeTx(i);
            await bridgeStore.AddBridgeAsync(tx);
            insertedBridges.Add(tx);
        }

        // ── Phase 3: Insert N=20 saga_steps rows ──────────────────────────────
        var insertedSagas = new List<SagaStepRecord>(N);
        for (var i = 0; i < N; i++)
        {
            var step = await sagaStore.EnqueueAsync(
                sagaName:           "G1DurabilityProbe",
                stepName:           $"Step{i:D2}",
                correlationKey:     DeterministicSagaCorr(i),
                stepIdempotencyKey: DeterministicSagaIdem(i),
                payloadJson:        $"{{\"g1_index\":{i}}}",
                isCompensation:     false,
                ct:                 CancellationToken.None);
            insertedSagas.Add(step);
        }

        // ── Phase 4: Hard-kill the configured container ────────────────────────
        KillContainerHard();

        // ── Phase 5: Restart + wait for /health 200 ───────────────────────────
        RestartContainer();
        await WaitForHealthAsync(TimeSpan.FromSeconds(30));

        // ── Phase 6: Rebuild the executor (connection recycled post-restart) ──
        // The old HttpClient's connection pool may be holding dead TCP sockets;
        // build a fresh one to avoid false TCP-reset failures in the assertions.
        var postKillExecutor    = CreateExecutor();
        var postKillBridgeStore = new SurrealBridgeStore(postKillExecutor);
        var postKillSagaStore   = new SurrealSagaStore(postKillExecutor);

        // ── Phase 7: Re-query all bridge_tx rows — assert byte-identical ───────
        // bridge_tx rows have no background mutator, so the post-restart row must
        // match the pre-kill snapshot field-for-field: a pure durability proof.
        for (var i = 0; i < N; i++)
        {
            var expected = insertedBridges[i];
            var actual   = await postKillBridgeStore.GetBridgeAsync(expected.Id);

            actual.Should().NotBeNull(
                $"bridge_tx row {expected.Id} must survive the hard kill (G1 per-commit fsync is required)");

            actual.Should().BeEquivalentTo(expected, opts => opts
                .Using<DateTime>(ctx => ctx.Subject.Should().BeCloseTo(ctx.Expectation, TimeSpan.FromSeconds(2)))
                .WhenTypeIs<DateTime>(),
                $"bridge_tx row {i} (id={expected.Id}) must be byte-identical after restart");
        }

        // ── Phase 8: Re-query all saga_steps rows — assert durability invariants ──
        // G1 proves the row I fsync'd is still on disk with its IDENTITY + PAYLOAD
        // intact. The saga PROCESSOR is now enabled by default (Phase A1:
        // Sagas:Enabled=true), and it legitimately mutates the lifecycle fields of
        // any enqueued step after restart — the "G1DurabilityProbe" saga has no
        // registered definition, so the processor dead-letters it (Status→
        // DeadLettered, AttemptCount++, LastError set, DeadLettered=true). That is
        // correct live behaviour, NOT a durability loss: the row survived the crash
        // (it exists, by id) and every write-once field is byte-identical.
        //
        // So assert the durability-INVARIANT fields (id, saga/step names,
        // correlation + idempotency keys, opaque payload, compensation flag,
        // CreatedAt) and EXCLUDE the processor-owned lifecycle fields the live
        // processor is entitled to change post-restart. Asserting those mutable
        // fields would test "is the processor disabled", not "did the write survive
        // the crash".
        for (var i = 0; i < N; i++)
        {
            var expected = insertedSagas[i];
            var actual   = await postKillSagaStore.GetAsync(expected.Id, CancellationToken.None);

            actual.Should().NotBeNull(
                $"saga_steps row {expected.Id} must survive the hard kill (G1 per-commit fsync is required)");

            actual.Should().BeEquivalentTo(expected, opts => opts
                .Using<DateTime>(ctx => ctx.Subject.Should().BeCloseTo(ctx.Expectation, TimeSpan.FromSeconds(2)))
                .WhenTypeIs<DateTime>()
                // Processor-owned lifecycle fields — mutated by the enabled saga
                // processor after restart (dead-letters the unregistered probe saga).
                .Excluding(s => s.Status)
                .Excluding(s => s.AttemptCount)
                .Excluding(s => s.NextRunAt)
                .Excluding(s => s.ClaimedAt)
                .Excluding(s => s.LastError)
                .Excluding(s => s.Output)
                .Excluding(s => s.DeadLettered)
                .Excluding(s => s.GateId)
                .Excluding(s => s.UpdatedAt),
                $"saga_steps row {i} (id={expected.Id}) durability-invariant fields " +
                "(id, names, keys, payload) must be byte-identical after restart");
        }
    }

    // ── Test 2: Static config assertion — FailsClosed without container ───────

    /// <summary>
    /// Reads <c>docker-compose.dev.yml</c> from the repo root and asserts the
    /// SurrealDB 3.x durable posture: the RocksDB engine URI
    /// (<c>rocksdb:///data/db</c>) plus <c>SURREAL_SYNC_DATA: "true"</c>, which
    /// makes RocksDB fsync its WAL on every commit. This is a static
    /// configuration proof — it runs without a live container and will catch a
    /// durability regression even before any container starts.
    ///
    /// The 1.5.x <c>surrealkv://...?sync=every</c> URI parameter is RETIRED
    /// (v3.1.4 cutover kept rocksdb; see DECISION.md). If a developer drops the
    /// <c>SURREAL_SYNC_DATA</c> flag or swaps to an in-memory / non-durable
    /// engine, this test fails immediately, surfacing the G1 violation in the
    /// earliest CI phase (pre-container, compile+config stage).
    /// </summary>
    [SkippableFact]
    public void G1_DurabilityAckGate_FailsClosed_IfSyncEventual()
    {
        // This test intentionally does NOT skip on container availability.
        // It is a static file assertion and must pass on every developer machine.

        var repoRoot    = FindLocalRepoRoot();
        var composeFile = Path.Combine(repoRoot, "docker-compose.dev.yml");

        File.Exists(composeFile).Should().BeTrue(
            $"docker-compose.dev.yml must exist at repo root ({composeFile}). " +
            "If the file was renamed, update the G1 config gate path.");

        var content = File.ReadAllText(composeFile);

        // Durable posture on 3.x = a persistent on-disk RocksDB engine, NOT an
        // in-memory or non-durable store. `memory://` or a missing rocksdb URI
        // would silently drop crash durability.
        content.Should().Contain("rocksdb://",
            "docker-compose.dev.yml must start SurrealDB against a persistent RocksDB " +
            "store (rocksdb:///data/db). An in-memory or non-durable engine violates " +
            "guardrail G1 (crash durability).");

        // The authoritative 3.x G1 durability signal: SURREAL_SYNC_DATA drives
        // RocksDB per-commit WAL fsync-before-ack. This REPLACES the retired
        // 1.5.x `surrealkv://...?sync=every` URI parameter.
        content.Should().Contain("SURREAL_SYNC_DATA: \"true\"",
            "docker-compose.dev.yml must set SURREAL_SYNC_DATA: \"true\" on the SurrealDB " +
            "container so RocksDB fsyncs its WAL on every commit before ACK. Removing this " +
            "flag disables fsync-before-ack and violates guardrail G1 (crash durability). " +
            "This test fails closed so a regression is caught before deployment, not after " +
            "a SIGKILL data-loss incident in production.");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a per-test ISurrealExecutor pointing at <see cref="IntegrationTestBase.TestNamespace"/>.
    /// Mirrors the pattern in SurrealBridgeStoreTests.CreateExecutorAsync verbatim.
    /// </summary>
    private ISurrealExecutor CreateExecutor()
    {
        var options = new SurrealConnectionOptions
        {
            Endpoint  = SurrealTestDefaults.Endpoint,
            Namespace = TestNamespace,
            Database  = "test",
            User      = SurrealTestDefaults.User,
            Password  = SurrealTestDefaults.Password,
        };
        var http       = new HttpClient();
        var connection = new HttpSurrealConnection(http, options);
        return new DefaultSurrealExecutor(connection);
    }

    /// <summary>
    /// Generates a deterministic <see cref="BridgeTransactionResult"/> for index
    /// <paramref name="i"/> using a stable, predictable id so post-restart reads
    /// can reconstruct the expected record without any in-memory state.
    /// </summary>
    private static BridgeTransactionResult MakeBridgeTx(int i)
    {
        // Id is a stable string that does not contain hyphens — SurrealDB
        // record ids must be identifier-safe strings.
        var id = $"g1bridge{i:D2}";
        return new BridgeTransactionResult
        {
            Id             = id,
            AvatarId       = DeriveGuid(SeedBase, i),
            SourceChain    = "Algorand",
            TargetChain    = "Solana",
            SourceTokenId  = $"ASA:{1000 + i}",
            TargetTokenId  = null,
            SourceAddress  = $"G1_SRC_{id}",
            TargetAddress  = $"G1_TGT_{id}",
            Amount         = 100 + i,
            Status         = BridgeStatus.Initiated,
            Mode           = BridgeMode.Trusted,
            CreatedAt      = DateTime.UtcNow,
            IdempotencyKey = $"g1-idem-{id}",
        };
    }

    private static string DeterministicSagaCorr(int i) => $"g1-corr-{i:D2}";
    private static string DeterministicSagaIdem(int i) => $"g1-idem-saga-{i:D2}";

    /// <summary>
    /// Derives a deterministic <see cref="Guid"/> from a base Guid + integer
    /// offset by XOR-ing the last 4 bytes with the offset. Produces N unique
    /// Guids from a single seed without requiring a random number generator.
    /// </summary>
    private static Guid DeriveGuid(Guid base_, int offset)
    {
        var bytes = base_.ToByteArray();
        // XOR the last two bytes with the low/high byte of the offset.
        bytes[14] ^= (byte)(offset & 0xFF);
        bytes[15] ^= (byte)((offset >> 8) & 0xFF);
        return new Guid(bytes);
    }

    /// <summary>
    /// The container defaults to the local compose name and may be injected by CI.
    /// </summary>
    private static string SurrealContainerName =>
        Environment.GetEnvironmentVariable("AZOA_SURREALDB_CONTAINER_NAME") ?? "azoa-dev-surrealdb";

    private static string ContainerRuntime =>
        Environment.GetEnvironmentVariable("AZOA_CONTAINER_RUNTIME") ?? "podman";

    /// <summary>
    /// Hard-kills the configured SurrealDB container.
    /// </summary>
    private static void KillContainerHard()
    {
        using var proc = RunContainer($"kill --signal=KILL {SurrealContainerName}");
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            var stderr = proc.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"{ContainerRuntime} kill --signal=KILL {SurrealContainerName} exited with code {proc.ExitCode}. " +
                $"stderr: {stderr}. Ensure the container is running before the Chaos test.");
        }
    }

    /// <summary>
    /// Restarts the configured SurrealDB container after its hard kill.
    /// </summary>
    private static void RestartContainer()
    {
        using var proc = RunContainer($"start {SurrealContainerName}");
        proc.WaitForExit();
        // Non-zero exit is not fatal here — the /health poll is authoritative.
    }

    /// <summary>Starts the configured container runtime with captured output.</summary>
    private static Process RunContainer(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = ContainerRuntime,
            Arguments              = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        return Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start `{ContainerRuntime} {arguments}`.");
    }

    /// <summary>
    /// Polls <c>{SurrealTestDefaults.Endpoint}/health</c> every 250 ms until 200 OK is
    /// returned or <paramref name="timeout"/> elapses. Throws a clear timeout
    /// exception so CI logs show "container did not come back" rather than
    /// a cryptic connection-refused error on the first store query.
    /// </summary>
    private static async Task WaitForHealthAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var r = await probe.GetAsync($"{SurrealTestDefaults.Endpoint}/health");
                if (r.IsSuccessStatusCode) return;
            }
            catch
            {
                // Container not yet accepting connections — keep polling.
            }

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"SurrealDB container at {SurrealTestDefaults.Endpoint} did not return HTTP 200 on /health " +
            $"within {timeout.TotalSeconds:F0} seconds after restart. " +
            $"Check {ContainerRuntime} logs for {SurrealContainerName}.");
    }

    /// <summary>
    /// Walks parent directories from the test assembly output path until it
    /// finds the repo root (identified by the presence of
    /// <c>AZOA.WebAPI.csproj</c>). This replicates the private
    /// <c>FindRepoRoot</c> logic in <see cref="IntegrationTestBase"/> locally
    /// so the G1 config-gate test can locate <c>docker-compose.dev.yml</c>
    /// without breaking base-class encapsulation.
    /// </summary>
    private static string FindLocalRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AZOA.WebAPI.csproj")))
                return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Cannot locate repo root (directory containing AZOA.WebAPI.csproj). " +
            "Ensure the test assembly is built from within the azoa repository.");
    }
}
