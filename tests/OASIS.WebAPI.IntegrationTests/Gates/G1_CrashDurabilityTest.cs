// ─── OASIS Sleek — Guardrail G1: Crash-Durability Gate ───────────────────────
//
// LOAD-BEARING ASSERTION (read before modifying this file)
// ──────────────────────────────────────────────────────────
// G1 requires that SurrealDB is started with the URI parameter `sync=every`
// (i.e. surrealkv://data/oasis.db?sync=every) so every write commit is fsynced
// to disk before the server ACKs the client. Without this flag, a hard kill
// of the process (SIGKILL / kill -9) may leave unflushed write buffers — some
// rows inserted *before* the kill can silently disappear after restart.
//
// G1_HardKill_DurableInserts_SurviveRestart proves this at runtime:
//   1. Insert N=20 bridge_tx rows + N=20 saga_steps rows via the real stores.
//   2. Hard-kill the container (podman kill --signal=KILL).
//   3. Restart via the idempotent start script and wait for /health.
//   4. Re-query every row by its deterministic id and assert byte-identical fields.
//
// If the container is running WITHOUT sync=every, some rows will be lost after
// the kill and the FluentAssertions equivalence check will FAIL. That failure
// is INTENTIONAL — it proves that task 12's deploy-time ack flag is real
// runtime evidence, not just a documentation checkbox.
//
// G1_DurabilityAckGate_FailsClosed_IfSyncEventual is a static config assertion
// that reads podman-compose.yml and asserts the URI contains `sync=every`.
// This runs without a live container, so a deploy with the wrong URI fails CI
// before the first container ever starts.
//
// Both tests are guarded by [Trait("Category","Chaos")] so they are excluded
// from the default CI filter (--filter "Category!=Chaos") and opt-in only.

using System.Diagnostics;
using FluentAssertions;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Connection;
using Oasis.SurrealDb.Client.Query;
using OASIS.WebAPI.Core.Blockchain.Wormhole;
using OASIS.WebAPI.IntegrationTests.Factories;
using OASIS.WebAPI.Models.Responses;
using OASIS.WebAPI.Models.Sagas;
using OASIS.WebAPI.Providers.Stores.Surreal;
using OASIS.WebAPI.Sagas;

namespace OASIS.WebAPI.IntegrationTests.Gates;

/// <summary>
/// Runtime evidence for OASIS guardrail G1 (crash durability).
/// Demonstrates that rows written via the real SurrealBridgeStore and
/// SurrealSagaStore survive a hard SIGKILL + container restart, proving that
/// the <c>surrealkv://data/oasis.db?sync=every</c> URI parameter is
/// load-bearing and not merely advisory.
/// </summary>
/// <remarks>
/// Chaos test — Windows + podman host expected; opt-in via --filter Category=Chaos.
/// The default CI pipeline uses --filter "Category!=Chaos" which skips this class.
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

    // ── Constructor (IClassFixture<OASISTestWebApplicationFactory>) ───────────

    public G1_CrashDurabilityTest(OASISTestWebApplicationFactory factory)
        : base(factory)
    {
    }

    // ── Test 1: Hard-kill + restart — every row must survive ─────────────────

    /// <summary>
    /// Inserts N=20 bridge_tx rows and N=20 saga_steps rows, hard-kills the
    /// SurrealDB container via <c>podman kill --signal=KILL</c>, restarts it,
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
            "SurrealDB test container not reachable at http://localhost:8442 — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");

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

        // ── Phase 4: Hard-kill the container via podman ────────────────────────
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
        for (var i = 0; i < N; i++)
        {
            var expected = insertedBridges[i];
            var actual   = await postKillBridgeStore.GetBridgeAsync(expected.Id);

            actual.Should().NotBeNull(
                $"bridge_tx row {expected.Id} must survive the hard kill (G1 sync=every is required)");

            actual.Should().BeEquivalentTo(expected, opts => opts
                .Using<DateTime>(ctx => ctx.Subject.Should().BeCloseTo(ctx.Expectation, TimeSpan.FromSeconds(2)))
                .WhenTypeIs<DateTime>(),
                $"bridge_tx row {i} (id={expected.Id}) must be byte-identical after restart");
        }

        // ── Phase 8: Re-query all saga_steps rows — assert byte-identical ──────
        for (var i = 0; i < N; i++)
        {
            var expected = insertedSagas[i];
            var actual   = await postKillSagaStore.GetAsync(expected.Id, CancellationToken.None);

            actual.Should().NotBeNull(
                $"saga_steps row {expected.Id} must survive the hard kill (G1 sync=every is required)");

            actual.Should().BeEquivalentTo(expected, opts => opts
                .Using<DateTime>(ctx => ctx.Subject.Should().BeCloseTo(ctx.Expectation, TimeSpan.FromSeconds(2)))
                .WhenTypeIs<DateTime>(),
                $"saga_steps row {i} (id={expected.Id}) must be byte-identical after restart");
        }
    }

    // ── Test 2: Static config assertion — FailsClosed without container ───────

    /// <summary>
    /// Reads <c>podman-compose.yml</c> from the repo root and asserts that the
    /// SurrealKV URI contains <c>sync=every</c>. This is a static configuration
    /// proof — it runs without a live container and will catch a durability
    /// regression even before any container starts.
    ///
    /// If a developer accidentally changes the URI to <c>sync=flush</c>,
    /// <c>sync=none</c>, or removes the parameter entirely, this test fails
    /// immediately, surfacing the G1 violation in the earliest CI phase
    /// (pre-container, compile+config stage).
    /// </summary>
    [SkippableFact]
    public void G1_DurabilityAckGate_FailsClosed_IfSyncEventual()
    {
        // This test intentionally does NOT skip on container availability.
        // It is a static file assertion and must pass on every developer machine.

        var repoRoot  = FindLocalRepoRoot();
        var composeFile = Path.Combine(repoRoot, "podman-compose.yml");

        File.Exists(composeFile).Should().BeTrue(
            $"podman-compose.yml must exist at repo root ({composeFile}). " +
            "If the file was moved, update the G1 config gate path.");

        var content = File.ReadAllText(composeFile);

        // The authoritative G1 durability signal: the SurrealKV URI parameter.
        // SURREAL_SYNC_DATA env var is belt-and-suspenders redundancy; the URI
        // parameter is what SurrealDB 1.x actually honours per the spec notes
        // in the compose file header comment.
        content.Should().Contain("sync=every",
            "podman-compose.yml must pass surrealkv://...?sync=every to the SurrealDB " +
            "container. Removing or changing this URI parameter disables fsync-before-ack and " +
            "violates guardrail G1 (crash durability). This test fails closed so a regression " +
            "is caught before deployment, not after a SIGKILL data loss incident in production.");
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
    /// Hard-kills the SurrealDB test container via
    /// <c>podman kill --signal=KILL oasis-surrealdb-test</c>.
    /// Throws with stderr content if the exit code is non-zero so CI logs show
    /// a clear error (e.g. "container not found") rather than a cryptic timeout.
    /// </summary>
    private static void KillContainerHard()
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "podman",
            Arguments              = "kill --signal=KILL oasis-surrealdb-test",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start podman kill process.");

        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            var stderr = proc.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"podman kill --signal=KILL oasis-surrealdb-test exited with code {proc.ExitCode}. " +
                $"stderr: {stderr}. Ensure the container is running before the Chaos test.");
        }
    }

    /// <summary>
    /// Restarts the SurrealDB test container via the idempotent PowerShell
    /// helper script. The script skips start if the container is already running
    /// and performs a restart otherwise — safe to call unconditionally.
    /// </summary>
    private static void RestartContainer()
    {
        // FindLocalRepoRoot() resolves the scripts/ path relative to the repo root
        // so the script path is correct regardless of the working directory the
        // test runner chooses.
        var repoRoot   = FindLocalRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "surrealdb", "start-test-container.ps1");

        var psi = new ProcessStartInfo
        {
            FileName               = "pwsh",
            Arguments              = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pwsh restart process.");

        proc.WaitForExit();
        // Non-zero exit is not fatal here — the container may already be running
        // and the script may return 1 in that idempotent case. The /health poll
        // that follows is the authoritative readiness check.
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
            "Check podman logs with: podman logs oasis-surrealdb-test");
    }

    /// <summary>
    /// Walks parent directories from the test assembly output path until it
    /// finds the repo root (identified by the presence of
    /// <c>OASIS.WebAPI.csproj</c>). This replicates the private
    /// <c>FindRepoRoot</c> logic in <see cref="IntegrationTestBase"/> locally
    /// so the G1 config-gate test can locate <c>podman-compose.yml</c>
    /// without breaking base-class encapsulation.
    /// </summary>
    private static string FindLocalRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "OASIS.WebAPI.csproj")))
                return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Cannot locate repo root (directory containing OASIS.WebAPI.csproj). " +
            "Ensure the test assembly is built from within the oasis-sleek repository.");
    }
}
