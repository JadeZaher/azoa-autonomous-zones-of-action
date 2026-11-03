// G1 acknowledged-write durability; see Gates/AGENTS.md.

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
        var containerReady = IsCrashContainerReady();
        if (!containerReady && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CI")))
            throw new InvalidOperationException(
                $"G1 requires a running {ContainerRuntime} container named '{SurrealContainerName}' in CI.");
        Skip.IfNot(containerReady,
            $"G1 requires a running {ContainerRuntime} container named '{SurrealContainerName}'.");

        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            $"SurrealDB container not reachable at {SurrealTestDefaults.Endpoint} — start the dev stack via `./dev-up.ps1` (brings up the `azoa-dev-surrealdb` legacy development container).");

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
        var connection = new SurrealDbNetConnection(options);
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
            UserId       = DeriveGuid(SeedBase, i),
            SourceChain    = "Algorand",
            TargetChain    = "Solana",
            SourceTokenId  = $"ASA:{1000 + i}",
            TargetTokenId  = null,
            SourceAddress  = $"G1_SRC_{id}",
            TargetAddress  = $"G1_TGT_{id}",
            Amount         = 100UL + (ulong)i,
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

    /// <summary>Checks that the named runtime container is running before the destructive chaos test begins.</summary>
    private static bool IsCrashContainerReady()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ContainerRuntime,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("inspect");
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("{{.State.Running}}");
            psi.ArgumentList.Add(SurrealContainerName);

            using var process = Process.Start(psi);
            if (process is null)
                return false;
            if (!process.WaitForExit((int)TimeSpan.FromSeconds(5).TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0
                && string.Equals(process.StandardOutput.ReadToEnd().Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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


}
