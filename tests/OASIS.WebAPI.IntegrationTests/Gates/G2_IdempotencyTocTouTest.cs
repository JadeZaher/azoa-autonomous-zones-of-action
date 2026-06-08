using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Connection;
using Oasis.SurrealDb.Client.Query;
using OASIS.WebAPI.Core.Blockchain.Wormhole;
using OASIS.WebAPI.IntegrationTests.Factories;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Bridge;
using OASIS.WebAPI.Models.Responses;
using OASIS.WebAPI.Models.Sagas;
using OASIS.WebAPI.Providers.Stores.Surreal;
using OASIS.WebAPI.Sagas;

namespace OASIS.WebAPI.IntegrationTests.Gates;

/// <summary>
/// G2 Gate tests — idempotency + single-field conditional state transitions.
///
/// <para><b>Test 1 — IdempotencyKey_50ConcurrentRedeemRequests_ProduceExactlyOneChainEffect</b>
/// proves the HTTP-layer idempotency contract: 50 concurrent POST /api/bridge/{id}/redeem
/// calls carrying the SAME Idempotency-Key header must produce exactly one on-chain
/// wormhole redemption call regardless of how many requests race past the controller
/// simultaneously. The IWormholeAdapter is stubbed with a thread-safe counter so the
/// "chain effect" count is observable without a real chain.</para>
///
/// <para><b>Test 2 — SagaSteps_TryClaimDueStep_20ConcurrentClaimers_ExactlyOneWins</b>
/// proves the store-level single-winner primitive: 20 concurrent
/// <see cref="ISagaStore.TryClaimDueStepAsync"/> calls on the SAME step must produce
/// exactly one winner (non-null return) and nineteen losers (null). Re-reading the
/// row must show Status=InProgress with ClaimedAt set.</para>
/// </summary>
[Trait("Category", "Gate")]
public sealed class G2_IdempotencyTocTouTest : IntegrationTestBase
{
    private readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web);

    public G2_IdempotencyTocTouTest(OASISTestWebApplicationFactory factory)
        : base(factory)
    {
    }

    // ── Test 1 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 50 concurrent POST /api/bridge/{id}/redeem calls carrying the same
    /// <c>Idempotency-Key</c> header must produce exactly ONE on-chain wormhole
    /// redemption and exactly ONE bridge_tx row in the Completed state.  All 49
    /// losers receive the same response body as the winner (idempotent replay).
    /// </summary>
    [SkippableFact]
    public async Task IdempotencyKey_50ConcurrentRedeemRequests_ProduceExactlyOneChainEffect()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            "SurrealDB test container not reachable — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");

        // ── 1. Counting wormhole adapter stub ────────────────────────────────
        // RedeemTransferAsync is the single on-chain call for a Wormhole redeem.
        // CountingWormholeAdapter is a hand-written stub (Moq is not available in
        // the integration-test project) with a thread-safe Interlocked counter so
        // concurrent callers can increment without locking.
        var wormholeStub = new CountingWormholeAdapter();
        // Alias for assertion clarity.
        Func<int> redeemCallCount = () => wormholeStub.RedeemCallCount;

        // ── 2. Per-test factory: isolated SurrealDB namespace + no rate limit +
        //       stubbed IWormholeAdapter. ───────────────────────────────────────
        // We need the app's SurrealDB stores to write into TestNamespace so the
        // direct-store verification queries land in the same isolated slice.
        var testNs = TestNamespace;
        await using var derivedFactory = Factory.WithWebHostBuilder(builder =>
        {
            // Point the app's SurrealDB stores at the per-test namespace that
            // IntegrationTestBase already created + schema-seeded.
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Redirect the app's SurrealDB stores to our isolated test namespace.
                    ["SurrealDb:Namespace"] = testNs,
                    ["SurrealDb:Database"]  = "test",
                    // Disable rate limiting so all 50 concurrent requests reach the
                    // service layer. Without this the "financial" policy (default
                    // PermitLimit=10) would reject 40 of the 50 with HTTP 429 before
                    // idempotency even runs.
                    ["RateLimiting:Enabled"] = "false",
                });
            });

            builder.ConfigureTestServices(services =>
            {
                // Re-apply the test-auth handler (the derived factory starts from
                // the base factory's ConfigureWebHost, which already installs it,
                // but WithWebHostBuilder creates a new builder chain so we must
                // re-register to keep authentication working).
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme    = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { });

                // Replace the real WormholeAdapter with our counting stub so
                // on-chain redemptions are observable without a real Guardian network.
                // AddHttpClient<IWormholeAdapter, WormholeAdapter> adds both a typed
                // client AND a scoped registration. We remove all existing
                // IWormholeAdapter registrations and replace with a singleton so all
                // 50 concurrent scopes share the SAME counter instance.
                var existingWormhole = services
                    .Where(d => d.ServiceType == typeof(IWormholeAdapter))
                    .ToList();
                foreach (var d in existingWormhole) services.Remove(d);
                services.AddSingleton<IWormholeAdapter>(wormholeStub);
            });
        });

        var client = derivedFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AuthHeaderName, "true");

        // ── 3. Seed a VAAReady bridge in the per-test namespace ───────────────
        // The bridge must have valid VaaBytes so the service can derive the
        // canonical VAA digest (SHA-256 over the base64-decoded bytes). We use
        // the same base64 payload as the unit tests.
        const string vaaBytes     = "VkFBLWcyLWlkZW1wb3RlbmN5LWdhdGU="; // arbitrary valid base64
        const string emitterAddr  = "0000000000000000000000000000000000000000000000000000000000000001";
        var bridgeId = $"g2_bridge_{Guid.NewGuid():N}";
        var avatarId = Guid.NewGuid();

        var executor = await CreateExecutorAsync(testNs);
        var bridgeStore = new SurrealBridgeStore(executor);

        await bridgeStore.AddBridgeAsync(new BridgeTransactionResult
        {
            Id                    = bridgeId,
            AvatarId              = avatarId,
            SourceChain           = "Solana",
            TargetChain           = "Algorand",
            SourceTokenId         = "token_g2",
            TargetAddress         = "recipient_g2",
            SourceAddress         = "source_g2",
            Amount                = 1,
            Mode                  = BridgeMode.Wormhole,
            Status                = BridgeStatus.VAAReady,
            VaaBytes              = vaaBytes,
            VaaSignatureCount     = 13,
            WormholeEmitterChainId   = 1,
            WormholeEmitterAddress   = emitterAddr,
            WormholeSequence         = 9001,
            CreatedAt             = DateTime.UtcNow,
            IdempotencyKey        = $"idem_{bridgeId}",
        });

        // ── 4. Pre-warm: one probe request so the first parallel caller is not
        //       burdened with cold-start connection and JIT overhead. ───────────
        // Use a GET /api/bridge/routes (non-financial, no rate limit even when
        // enabled) to warm up the HTTP client + connection pool without touching
        // the bridge row's state.
        var warmup = await client.GetAsync("/api/bridge/routes");
        _ = warmup; // outcome irrelevant; we only care about warming the pool

        // ── 5. 50 concurrent identical redeem requests ────────────────────────
        // All 50 share the same Idempotency-Key so the idempotency store elects
        // exactly one winner. The parallel section is wrapped in try/finally so
        // cleanup + assertions run even when some tasks throw.
        const string sharedIdempotencyKey = "gate-g2-concurrent-redeem-idem-key-001";
        const int concurrency = 50;

        HttpResponseMessage[] responses = Array.Empty<HttpResponseMessage>();
        try
        {
            var bag = new System.Collections.Concurrent.ConcurrentBag<HttpResponseMessage>();
            await Parallel.ForEachAsync(
                Enumerable.Range(0, concurrency),
                new ParallelOptions { MaxDegreeOfParallelism = concurrency },
                async (_, ct) =>
                {
                    // Each task builds its own request with the shared idempotency key.
                    var request = new HttpRequestMessage(
                        HttpMethod.Post,
                        $"/api/bridge/{bridgeId}/redeem");
                    request.Headers.Add(TestAuthHandler.AuthHeaderName, "true");
                    request.Headers.Add("Idempotency-Key", sharedIdempotencyKey);
                    var resp = await client.SendAsync(request, ct);
                    bag.Add(resp);
                });
            responses = bag.ToArray();
        }
        finally
        {
            // ── 6. Assertions ─────────────────────────────────────────────────

            // (a) Exactly one wormhole redemption call reached the chain.
            redeemCallCount().Should().Be(1,
                "the idempotency gate (IIdempotencyStore.TryClaimAsync) plus the "
                + "atomic VAAReady→Redeeming transition must elect exactly one "
                + "caller to perform the on-chain redemption — this is G2");

            // (b) Exactly one bridge_tx row exists in Completed state for this bridge id.
            // Re-read via direct store to confirm the row state is Completed (not
            // duplicated into multiple rows or left in an intermediate state).
            var finalBridge = await bridgeStore.GetBridgeAsync(bridgeId);
            finalBridge.Should().NotBeNull("the bridge row must still exist after concurrent redeems");
            finalBridge!.Status.Should().Be(BridgeStatus.Completed,
                "the winning caller must have driven the bridge to Completed");

            // (c) Idempotent replay contract: every non-error response must carry
            // the SAME redemption tx hash ("gate_g2_redeem_tx") as the winner.
            // Losers may be HTTP 400 (concurrent-reject or replay-error) or HTTP
            // 200 (idempotent replay of the prior completed result). No response
            // must reference a DIFFERENT tx hash (which would indicate a second mint).
            var successResponses = responses
                .Where(r => r.IsSuccessStatusCode)
                .ToList();

            successResponses.Should().NotBeEmpty(
                "at minimum the single winning caller must receive HTTP 200");

            foreach (var successResp in successResponses)
            {
                var body = await successResp.Content.ReadFromJsonAsync<BridgeTransactionResult>(_jsonOpts);
                body.Should().NotBeNull();
                body!.Status.Should().Be(BridgeStatus.Completed,
                    "every successful response is either the winner or an idempotent "
                    + "replay of the SAME single mint — the status must be Completed");
            }

            // No response must carry a DIFFERENT mint tx hash (that would evidence
            // a second distinct chain effect).
            var distinctHashes = successResponses
                .Select(async r => (await r.Content.ReadFromJsonAsync<BridgeTransactionResult>(_jsonOpts))?.RedemptionTxHash)
                .Select(t => t.GetAwaiter().GetResult())
                .Where(h => h is not null)
                .Distinct()
                .ToList();

            // Note: the response body after a JSON 400 won't have RedemptionTxHash;
            // only the 200s contribute to the distinct set above.
            distinctHashes.Should().ContainSingle(
                "all successful callers must reference the ONE canonical mint tx "
                + "('gate_g2_redeem_tx'), never a second distinct hash");
            distinctHashes[0].Should().Be("gate_g2_redeem_tx",
                "the single on-chain effect must be the stub's constant tx hash");

            foreach (var r in responses) r.Dispose();
        }
    }

    // ── Test 2 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 20 concurrent <see cref="ISagaStore.TryClaimDueStepAsync"/> calls on the
    /// SAME Pending step must produce exactly ONE non-null return (winner) and
    /// nineteen null returns (losers). Re-reading the row must show
    /// Status=InProgress with ClaimedAt set — confirming the conditional-UPDATE
    /// WHERE Status==Pending predicate provides the single-winner invariant at the
    /// SurrealDB engine level.
    /// </summary>
    [SkippableFact]
    public async Task SagaSteps_TryClaimDueStep_20ConcurrentClaimers_ExactlyOneWins()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            "SurrealDB test container not reachable — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");

        // ── 1. Bootstrap saga_steps schema in the per-test namespace ──────────
        // IntegrationTestBase.InitializeAsync applies .surql schemas from
        // Persistence/SurrealDb/Schemas/*.surql — saga_steps is 080_saga_steps.surql.
        // When the schema runner has already applied it this is a no-op (IF NOT EXISTS).
        // We bootstrap defensively in case the schema file was not present when
        // InitializeAsync ran.
        await BootstrapSagaSchemaAsync(TestNamespace);

        // ── 2. Build SurrealSagaStore against the per-test namespace ──────────
        var executor = await CreateExecutorAsync(TestNamespace);
        var sagaStore = new SurrealSagaStore(executor);

        // ── 3. Seed ONE Pending step that is immediately due ──────────────────
        var seeded = await sagaStore.EnqueueAsync(
            sagaName:          "G2GateTest",
            stepName:          "ClaimRace",
            correlationKey:    $"corr-{Guid.NewGuid():N}",
            stepIdempotencyKey: $"idem-{Guid.NewGuid():N}",
            payloadJson:       "{}",
            isCompensation:    false,
            ct:                CancellationToken.None);

        seeded.Should().NotBeNull();
        seeded.Status.Should().Be(StepStatus.Pending);

        // The step is enqueued with NextRunAt=now (due immediately). We use a
        // now+5s timestamp for the claim so all 20 racers agree the step is due.
        var claimNow = DateTime.UtcNow.AddSeconds(5);

        // ── 4. 20 concurrent TryClaimDueStepAsync calls ───────────────────────
        // Each task gets a dedicated executor so they do NOT share connection-level
        // state — this maximises interleaving and stress-tests the SurrealDB
        // atomic conditional UPDATE under true concurrency.
        const int concurrency = 20;

        var claimTasks = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(async () =>
            {
                // Each concurrent claimer uses its own executor (independent HTTP
                // connection) to avoid any client-side serialisation.
                var workerExecutor = await CreateExecutorAsync(TestNamespace);
                var workerStore    = new SurrealSagaStore(workerExecutor);
                return await workerStore.TryClaimDueStepAsync(
                    seeded.Id, claimNow, CancellationToken.None);
            }))
            .ToArray();

        var results = await Task.WhenAll(claimTasks);

        // ── 5. Assertions ─────────────────────────────────────────────────────

        var winners = results.Where(r => r is not null).ToList();
        var losers  = results.Where(r => r is null).ToList();

        winners.Should().HaveCount(1,
            "exactly one caller must win the conditional UPDATE WHERE status==Pending — "
            + "this is the single-winner invariant G2 proves at the SurrealDB engine level");

        losers.Should().HaveCount(concurrency - 1,
            "every other concurrent claimer must lose (see AffectedCount==0) because "
            + "the WHERE status==Pending predicate no longer matches once the winner transitions it");

        // The winning record must already show InProgress + ClaimedAt set.
        var winnerRecord = winners[0]!;
        winnerRecord.Id.Should().Be(seeded.Id);
        winnerRecord.Status.Should().Be(StepStatus.InProgress,
            "the winner's returned record must reflect the committed InProgress state");
        winnerRecord.ClaimedAt.Should().NotBeNull(
            "TryClaimDueStepAsync must set ClaimedAt on the winning row");

        // Re-read the row independently to confirm the store committed the state.
        var reread = await sagaStore.GetAsync(seeded.Id, CancellationToken.None);
        reread.Should().NotBeNull("the step row must still exist after concurrent claims");
        reread!.Status.Should().Be(StepStatus.InProgress,
            "the row must remain InProgress — losers must NOT regress the state");
        reread.ClaimedAt.Should().NotBeNull(
            "ClaimedAt must be persisted; the lease is how the saga processor tracks ownership");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Build an <see cref="ISurrealExecutor"/> scoped to the given SurrealDB
    /// namespace + "test" database. Mirrors the pattern established in
    /// <see cref="Persistence.Surreal.SurrealBridgeStoreTests"/>.
    /// </summary>
    private static Task<ISurrealExecutor> CreateExecutorAsync(string ns)
    {
        var options = new SurrealConnectionOptions
        {
            Endpoint  = SurrealTestDefaults.Endpoint,
            Namespace = ns,
            Database  = "test",
            User      = SurrealTestDefaults.User,
            Password  = SurrealTestDefaults.Password,
        };

        var http       = new HttpClient();
        var connection = new HttpSurrealConnection(http, options);
        var executor   = new DefaultSurrealExecutor(connection);
        return Task.FromResult<ISurrealExecutor>(executor);
    }

    /// <summary>
    /// Applies the minimal <c>saga_steps</c> DDL to the given test namespace,
    /// mirroring the bootstrap used by <see cref="Persistence.Surreal.SurrealSagaStoreTests"/>.
    /// Safe to call when the table already exists (uses IF NOT EXISTS).
    /// </summary>
    private static async Task BootstrapSagaSchemaAsync(string testNamespace)
    {
        using var ddlClient = new HttpClient
        {
            BaseAddress = new Uri(SurrealTestDefaults.Endpoint)
        };
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));
        ddlClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        ddlClient.DefaultRequestHeaders.Add("NS", testNamespace);
        ddlClient.DefaultRequestHeaders.Add("DB", "test");

        // Mirrors 080_saga_steps.surql — IF NOT EXISTS is idempotent.
        const string ddl = """
            DEFINE TABLE IF NOT EXISTS saga_steps SCHEMAFULL;
            DEFINE FIELD IF NOT EXISTS id                   ON saga_steps TYPE string ASSERT $value != NONE AND $value != "";
            DEFINE FIELD IF NOT EXISTS correlation_key      ON saga_steps TYPE string ASSERT $value != NONE AND $value != "";
            DEFINE FIELD IF NOT EXISTS saga_name            ON saga_steps TYPE string ASSERT $value != NONE AND $value != "";
            DEFINE FIELD IF NOT EXISTS step_name            ON saga_steps TYPE string ASSERT $value != NONE AND $value != "";
            DEFINE FIELD IF NOT EXISTS step_idempotency_key ON saga_steps TYPE string ASSERT $value != NONE AND $value != "";
            DEFINE FIELD IF NOT EXISTS payload              ON saga_steps TYPE string;
            DEFINE FIELD IF NOT EXISTS status               ON saga_steps TYPE string DEFAULT "Pending" ASSERT $value INSIDE ["Pending","InProgress","Completed","Compensating","DeadLettered"];
            DEFINE FIELD IF NOT EXISTS is_compensation      ON saga_steps TYPE bool DEFAULT false;
            DEFINE FIELD IF NOT EXISTS attempt_count        ON saga_steps TYPE int DEFAULT 0;
            DEFINE FIELD IF NOT EXISTS next_run_at          ON saga_steps TYPE datetime;
            DEFINE FIELD IF NOT EXISTS claimed_at           ON saga_steps FLEXIBLE TYPE option<datetime>;
            DEFINE FIELD IF NOT EXISTS last_error           ON saga_steps FLEXIBLE TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS output               ON saga_steps FLEXIBLE TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS dead_lettered        ON saga_steps TYPE bool DEFAULT false;
            DEFINE FIELD IF NOT EXISTS created_at           ON saga_steps TYPE datetime;
            DEFINE FIELD IF NOT EXISTS updated_at           ON saga_steps TYPE datetime;
            DEFINE INDEX IF NOT EXISTS saga_steps_correlation_key   ON saga_steps FIELDS correlation_key;
            DEFINE INDEX IF NOT EXISTS saga_steps_due_scan          ON saga_steps FIELDS status, next_run_at;
            DEFINE INDEX IF NOT EXISTS saga_steps_lease_scan        ON saga_steps FIELDS status, claimed_at;
            DEFINE INDEX IF NOT EXISTS saga_steps_idempotency_key   ON saga_steps FIELDS step_idempotency_key
            """;

        var content  = new StringContent(ddl, System.Text.Encoding.UTF8, "text/plain");
        var response = await ddlClient.PostAsync("/sql", content);
        _ = response; // best-effort; InitializeAsync may have applied it already
    }
}

/// <summary>
/// Hand-written counting stub for <see cref="IWormholeAdapter"/>.
/// All methods that are not exercised by the G2 redeem path return no-op
/// results. <see cref="RedeemTransferAsync"/> is the observable "chain effect"
/// counter — it increments <see cref="RedeemCallCount"/> atomically via
/// <see cref="Interlocked.Increment"/> and returns a fixed success result so
/// the bridge service can advance the row to Completed.
///
/// Moq is not available in the integration-tests project (the csproj does not
/// reference it), so this hand-written stub fills the same role without external
/// dependencies.
/// </summary>
internal sealed class CountingWormholeAdapter : IWormholeAdapter
{
    private int _redeemCallCount;

    /// <summary>How many times <see cref="RedeemTransferAsync"/> was invoked.</summary>
    public int RedeemCallCount => _redeemCallCount;

    public Task<OASISResult<WormholeRedemptionResult>> RedeemTransferAsync(
        string targetChain, WormholeVAA vaa, string recipientAddress,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _redeemCallCount);
        return Task.FromResult(new OASISResult<WormholeRedemptionResult>
        {
            IsError = false,
            Result  = new WormholeRedemptionResult { TxHash = "gate_g2_redeem_tx", Success = true }
        });
    }

    public Task<OASISResult<WormholeTransferInitiation>> InitiateTransferAsync(
        string sourceChain, string targetChain, string tokenId,
        string senderAddress, string recipientAddress, int amount,
        CancellationToken ct = default)
        => Task.FromResult(new OASISResult<WormholeTransferInitiation>
        {
            IsError = true, Message = "CountingWormholeAdapter: InitiateTransfer not stubbed for G2 test"
        });

    public Task<OASISResult<WormholeVAA>> FetchVAAAsync(
        int emitterChainId, string emitterAddress, long sequence,
        CancellationToken ct = default)
        => Task.FromResult(new OASISResult<WormholeVAA>
        {
            IsError = true, Message = "CountingWormholeAdapter: FetchVAA not stubbed for G2 test"
        });

    public Task<OASISResult<bool>> VerifyVAAAsync(WormholeVAA vaa, CancellationToken ct = default)
        => Task.FromResult(new OASISResult<bool> { IsError = false, Result = true });

    public int? GetWormholeChainId(string oasisChainName) => null;

    public bool IsRouteSupported(string sourceChain, string targetChain) => true;
}
