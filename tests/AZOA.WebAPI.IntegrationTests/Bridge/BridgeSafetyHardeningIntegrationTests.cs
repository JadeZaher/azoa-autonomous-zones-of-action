using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Connection;
using Azoa.SurrealDb.Client.Idempotency;
using Azoa.SurrealDb.Client.Query;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Bridge;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores.Surreal;

namespace AZOA.WebAPI.IntegrationTests.Bridge;

/// <summary>
/// Phase C item 1 (integration variant) — bridge-safety-hardening track.
///
/// <para>
/// These tests prove DB-level guarantees (UNIQUE constraints, conditional-UPDATE
/// atomicity) that unit-test mocks cannot exercise. All four tests bypass the
/// HTTP controller where the test target is store-level semantics; the HTTP
/// double-redeem test uses the derived-factory pattern established in G2.
/// </para>
///
/// <para>
/// Kill-switch note: the factory applies Blockchain:Bridge:RealValueEnabled=true
/// for the HTTP double-redeem test (Test 2) because the seeded bridge row uses
/// real chain names (Solana/Algorand) and IntegrationTest env resolves
/// Blockchain:Mode=Live (no Development override). Simulated routes would also
/// satisfy the gate but require separate chain-type config plumbing; the flag
/// override is the minimal one-line fix (same pattern as G2's RateLimiting:Enabled).
/// </para>
///
/// <para>
/// SurrealDB container required: tests skip gracefully via
/// <see cref="SkipIfSurrealDbUnavailableAsync"/> when the container is absent.
/// Start it with: <c>pwsh scripts/surrealdb/start-test-container.ps1</c>
/// </para>
/// </summary>
[Trait("Category", "BridgeSafetyHardening")]
public sealed class BridgeSafetyHardeningIntegrationTests : IntegrationTestBase
{
    private readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public BridgeSafetyHardeningIntegrationTests(AZOATestWebApplicationFactory factory)
        : base(factory)
    {
    }

    // ── Test 1a: Consumed-VAA digest UNIQUE race ──────────────────────────────

    /// <summary>
    /// Two concurrent <see cref="SurrealBridgeStore.TryInsertConsumedVaaAsync"/>
    /// calls carrying the SAME digest → exactly one returns true; the other
    /// returns false (UNIQUE constraint on digest, no thrown exception).
    /// Proves the SurrealDB engine-level insert-wins guarantee that the
    /// unit-test mock cannot exercise.
    /// </summary>
    [SkippableFact]
    public async Task TryInsertConsumedVaa_ConcurrentSameDigest_ExactlyOneWins()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            "SurrealDB test container not reachable — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");

        // Each concurrent caller gets its own executor (independent HTTP
        // connection) to maximise interleaving under real SurrealDB concurrency.
        var storeA = await CreateBridgeStoreAsync();
        var storeB = await CreateBridgeStoreAsync();

        var digest = MakeDigest("race_digest_same_001");
        var record = new ConsumedVaaRecord
        {
            Digest              = digest,
            EmitterChainId      = 1,
            EmitterAddress      = MakeEmitterAddress("race_emitter_a"),
            Sequence            = 1001,
            BridgeTransactionId = "br_race_same_digest",
            ConsumedAt          = DateTime.UtcNow,
        };

        var recordB = new ConsumedVaaRecord
        {
            Digest              = digest,
            EmitterChainId      = 1,
            EmitterAddress      = MakeEmitterAddress("race_emitter_a"),
            Sequence            = 1001,
            BridgeTransactionId = "br_race_same_digest_b",
            ConsumedAt          = DateTime.UtcNow,
        };

        var results = await Task.WhenAll(
            storeA.TryInsertConsumedVaaAsync(record),
            storeB.TryInsertConsumedVaaAsync(recordB));

        var wins   = results.Count(r => r);
        var losses = results.Count(r => !r);

        wins.Should().Be(1,
            "UNIQUE(digest) insert-wins must elect exactly one caller regardless of concurrency");
        losses.Should().Be(1,
            "the losing concurrent insert must receive false, not an exception");
    }

    // ── Test 1b: Consumed-VAA emitter-triple UNIQUE race ─────────────────────

    /// <summary>
    /// Two inserts carrying DIFFERENT digests but the SAME
    /// (EmitterChainId, EmitterAddress, Sequence) triple → the second insert
    /// returns false (UNIQUE on triple index), confirming the second index is
    /// enforced at the DB level independently of the digest index.
    /// </summary>
    [SkippableFact]
    public async Task TryInsertConsumedVaa_SameEmitterTripleDifferentDigest_SecondReturnsFalse()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            "SurrealDB test container not reachable — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");

        var store = await CreateBridgeStoreAsync();

        var first = new ConsumedVaaRecord
        {
            Digest              = MakeDigest("triple_first_digest"),
            EmitterChainId      = 7,
            EmitterAddress      = MakeEmitterAddress("triple_emitter"),
            Sequence            = 42,
            BridgeTransactionId = "br_triple_first",
            ConsumedAt          = DateTime.UtcNow,
        };
        // Same triple, distinct digest — must be rejected by the emitter triple index.
        var second = new ConsumedVaaRecord
        {
            Digest              = MakeDigest("triple_second_digest"),
            EmitterChainId      = 7,
            EmitterAddress      = MakeEmitterAddress("triple_emitter"),
            Sequence            = 42,
            BridgeTransactionId = "br_triple_second",
            ConsumedAt          = DateTime.UtcNow,
        };

        var firstResult  = await store.TryInsertConsumedVaaAsync(first);
        var secondResult = await store.TryInsertConsumedVaaAsync(second);

        firstResult.Should().BeTrue("first insert with a unique triple must succeed");
        secondResult.Should().BeFalse(
            "UNIQUE(emitter_chain_id, emitter_address, sequence) must reject a second "
            + "insert even when the digest differs — guards against sequence-replay attacks");
    }

    // ── Test 2: Concurrent double-redeem via HTTP ─────────────────────────────

    /// <summary>
    /// Two parallel POST /api/bridge/{id}/redeem requests for the SAME VAAReady
    /// bridge → exactly one reaches the on-chain adapter; the other receives a
    /// deterministic duplicate/park response; the bridge row ends Completed exactly
    /// once.
    ///
    /// <para>
    /// IWormholeAdapter is replaced with a counting stub (Moq is not available in
    /// this project — same precedent as G2's CountingWormholeAdapter). The kill
    /// switch is overridden to true because the seeded chains (Solana/Algorand) are
    /// non-simulated under IntegrationTest's Live Blockchain:Mode.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task ConcurrentRedeem_SameVAAReadyBridge_ExactlyOneOnChainCallAndCompletedRow()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            "SurrealDB test container not reachable — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");

        // ── 1. Counting adapter stub ─────────────────────────────────────────
        var wormholeStub = new BridgeSafetyCountingWormholeAdapter();
        var testNs = TestNamespace;

        // ── 2. Derived factory: isolated NS + kill-switch on + stubbed adapter ─
        // Kill switch override: bridge-safety-hardening track. The IntegrationTest
        // environment resolves Blockchain:Mode=Live (no Development overlay), so
        // Solana/Algorand chains are non-simulated → RealValueEnabled must be true
        // or the service refuses before the idempotency gate is ever reached.
        await using var derivedFactory = Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SurrealDb:Namespace"]                 = testNs,
                    ["SurrealDb:Database"]                  = "test",
                    ["RateLimiting:Enabled"]                = "false",
                    // bridge-safety-hardening: enable real-value so non-simulated routes
                    // reach the idempotency+consumed-VAA gate (the subject under test).
                    ["Blockchain:Bridge:RealValueEnabled"]  = "true",
                });
            });

            builder.ConfigureTestServices(services =>
            {
                // Re-apply test auth (WithWebHostBuilder rebuilds the pipeline).
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme    = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { });

                // Replace IWormholeAdapter with a thread-safe counting stub.
                var existing = services
                    .Where(d => d.ServiceType == typeof(IWormholeAdapter))
                    .ToList();
                foreach (var d in existing) services.Remove(d);
                services.AddSingleton<IWormholeAdapter>(wormholeStub);
            });
        });

        var client = derivedFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AuthHeaderName, "true");

        // ── 3. Seed a VAAReady bridge row directly into the test namespace ────
        const string vaaBytes    = "VkFBLXNhZmV0eS1oYXJkZW5pbmctY29uY3VycmVudA=="; // unique base64
        const string emitterAddr = "0000000000000000000000000000000000000000000000000000000000000002";
        var bridgeId = $"bsh_br_{Guid.NewGuid():N}";
        var avatarId = Guid.NewGuid();

        var executor    = await CreateExecutorAsync(testNs);
        var bridgeStore = new SurrealBridgeStore(executor);

        await bridgeStore.AddBridgeAsync(new BridgeTransactionResult
        {
            Id                       = bridgeId,
            AvatarId                 = avatarId,
            SourceChain              = "Solana",
            TargetChain              = "Algorand",
            SourceTokenId            = "token_bsh",
            TargetAddress            = "recipient_bsh",
            SourceAddress            = "source_bsh",
            Amount                   = 1,
            Mode                     = BridgeMode.Wormhole,
            Status                   = BridgeStatus.VAAReady,
            VaaBytes                 = vaaBytes,
            VaaSignatureCount        = 13,
            WormholeEmitterChainId   = 1,
            WormholeEmitterAddress   = emitterAddr,
            WormholeSequence         = 9002,
            CreatedAt                = DateTime.UtcNow,
            IdempotencyKey           = $"idem_{bridgeId}",
        });

        // Warm up the HTTP client before firing the concurrent pair.
        _ = await client.GetAsync("/api/bridge/routes");

        // ── 4. Two parallel redeem requests ──────────────────────────────────
        // Two concurrents are sufficient: the consumed-VAA UNIQUE insert + the
        // VAAReady→Redeeming conditional transition together elect exactly one.
        var sharedKey = $"bsh-concurrent-redeem-{Guid.NewGuid():N}";
        const int concurrency = 2;

        var bag = new System.Collections.Concurrent.ConcurrentBag<HttpResponseMessage>();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, concurrency),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (_, ct) =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"/api/bridge/{bridgeId}/redeem");
                req.Headers.Add(TestAuthHandler.AuthHeaderName, "true");
                req.Headers.Add("Idempotency-Key", sharedKey);
                bag.Add(await client.SendAsync(req, ct));
            });

        var responses = bag.ToArray();

        // ── 5. Assertions ─────────────────────────────────────────────────────
        try
        {
            // (a) Exactly one on-chain adapter call.
            wormholeStub.RedeemCallCount.Should().Be(1,
                "the idempotency claim + consumed-VAA UNIQUE insert elect exactly one caller "
                + "to perform the on-chain redemption; the second concurrent call must be "
                + "parked or replay-returned without calling the adapter again");

            // (b) Bridge row ends Completed exactly once.
            var finalBridge = await bridgeStore.GetBridgeAsync(bridgeId);
            finalBridge.Should().NotBeNull();
            finalBridge!.Status.Should().Be(BridgeStatus.Completed,
                "the winning caller must drive the row to Completed");

            // (c) At least one HTTP 200.
            var successes = responses.Where(r => r.IsSuccessStatusCode).ToList();
            successes.Should().NotBeEmpty("the winning caller must receive HTTP 200");

            // (d) No 2xx response may carry a second distinct redemption tx hash.
            var distinctHashes = new List<string?>();
            foreach (var r in successes)
            {
                var body = await r.Content.ReadFromJsonAsync<BridgeTransactionResult>(_jsonOpts);
                distinctHashes.Add(body?.RedemptionTxHash);
            }
            distinctHashes
                .Where(h => h is not null)
                .Distinct()
                .Should().ContainSingle(
                    "all successful responses must reference the single canonical "
                    + "tx hash — a second distinct hash would evidence a double-mint");
        }
        finally
        {
            foreach (var r in responses) r.Dispose();
        }
    }

    // ── Test 3: Idempotency claim race (store-level) ──────────────────────────

    /// <summary>
    /// Two concurrent <see cref="SurrealIdempotencyStore.TryClaimAsync"/> calls
    /// with the SAME key → exactly one returns Won=true; the other returns
    /// Won=false. Proves the SurrealDB insert-wins at the store level independently
    /// of the HTTP path.
    /// </summary>
    [SkippableFact]
    public async Task TryClaimAsync_ConcurrentSameKey_ExactlyOneWins()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            "SurrealDB test container not reachable — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");

        // Each concurrent caller gets its own executor to avoid connection-level
        // serialisation masking true concurrency.
        var executorA = await CreateExecutorAsync(TestNamespace);
        var executorB = await CreateExecutorAsync(TestNamespace);
        var storeA = new SurrealIdempotencyStore(executorA);
        var storeB = new SurrealIdempotencyStore(executorB);

        var sharedKey = $"bsh-claim-race-{Guid.NewGuid():N}";

        var claims = await Task.WhenAll(
            storeA.TryClaimAsync(sharedKey, "bridge_redeem", CancellationToken.None),
            storeB.TryClaimAsync(sharedKey, "bridge_redeem", CancellationToken.None));

        var winners = claims.Count(c => c.Won);
        var losers  = claims.Count(c => !c.Won);

        winners.Should().Be(1,
            "UNIQUE(key) insert-wins must elect exactly one claimer, regardless of concurrency; "
            + "this is the SurrealDB-level guarantee the unit mocks cannot exercise");
        losers.Should().Be(1,
            "the losing claimer must receive Won=false and the existing record — never an exception");

        // The loser's record must be the same key (re-read of the winner's row).
        claims[0].Record.Key.Should().Be(sharedKey);
        claims[1].Record.Key.Should().Be(sharedKey);
    }

    // ── Test 4a: bridge_tx Network field round-trip (set) ────────────────────

    /// <summary>
    /// A bridge row inserted with Network=Devnet round-trips via the store with
    /// the value preserved — proves the nullable ChainNetwork option field
    /// persists correctly in the SurrealDB schema.
    /// </summary>
    [SkippableFact]
    public async Task BridgeTx_NetworkSetToDevnet_RoundTripsCorrectly()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            "SurrealDB test container not reachable — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");

        var store    = await CreateBridgeStoreAsync();
        var bridgeId = $"bsh_net_set_{Guid.NewGuid():N}";

        await store.AddBridgeAsync(NewBridge(bridgeId, network: ChainNetwork.Devnet));

        var loaded = await store.GetBridgeAsync(bridgeId);

        loaded.Should().NotBeNull();
        loaded!.Network.Should().Be(ChainNetwork.Devnet,
            "Network=Devnet must survive the SurrealDB serialize/deserialize round-trip");
    }

    // ── Test 4b: bridge_tx Network field round-trip (null) ───────────────────

    /// <summary>
    /// A bridge row inserted with Network=null round-trips with null — proves
    /// the option&lt;string&gt; schema field back-compat for rows written before the
    /// Network column was added.
    /// </summary>
    [SkippableFact]
    public async Task BridgeTx_NetworkNull_RoundTripsAsNull()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            "SurrealDB test container not reachable — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");

        var store    = await CreateBridgeStoreAsync();
        var bridgeId = $"bsh_net_null_{Guid.NewGuid():N}";

        await store.AddBridgeAsync(NewBridge(bridgeId, network: null));

        var loaded = await store.GetBridgeAsync(bridgeId);

        loaded.Should().NotBeNull();
        loaded!.Network.Should().BeNull(
            "rows with no Network value must deserialize as null — option back-compat");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="SurrealBridgeStore"/> bound to the per-class test
    /// namespace. Mirrors the pattern in <see cref="SurrealBridgeStoreTests"/>.
    /// </summary>
    private async Task<SurrealBridgeStore> CreateBridgeStoreAsync()
        => new SurrealBridgeStore(await CreateExecutorAsync(TestNamespace));

    /// <summary>
    /// Builds a <see cref="ISurrealExecutor"/> scoped to the given namespace +
    /// "test" database. Mirrors G2's static helper (promoted here as instance
    /// helper to share TestNamespace).
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
        var connection = new HttpSurrealConnection(new HttpClient(), options);
        return Task.FromResult<ISurrealExecutor>(new DefaultSurrealExecutor(connection));
    }

    /// <summary>Minimal bridge row factory for round-trip tests.</summary>
    private static BridgeTransactionResult NewBridge(
        string id,
        BridgeStatus status = BridgeStatus.Initiated,
        ChainNetwork? network = null)
    {
        return new BridgeTransactionResult
        {
            Id             = id,
            AvatarId       = Guid.NewGuid(),
            SourceChain    = "Algorand",
            TargetChain    = "Solana",
            SourceTokenId  = "ASA:123",
            SourceAddress  = "SRC_" + id,
            TargetAddress  = "TGT_" + id,
            Amount         = 1_000,
            Status         = status,
            Mode           = BridgeMode.Trusted,
            CreatedAt      = DateTime.UtcNow,
            IdempotencyKey = "idem_" + id,
            Network        = network,
        };
    }

    /// <summary>Build a deterministic 128-char SHA-512 hex digest from a seed string.</summary>
    private static string MakeDigest(string seed)
    {
        var data = System.Security.Cryptography.SHA512.HashData(
            System.Text.Encoding.UTF8.GetBytes(seed));
        var sb = new System.Text.StringBuilder(128);
        foreach (var b in data) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>Canonical Wormhole 32-byte emitter address: 64 lowercase hex chars.</summary>
    private static string MakeEmitterAddress(string seed)
    {
        var data = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(seed));
        var sb = new System.Text.StringBuilder(64);
        foreach (var b in data) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}

/// <summary>
/// Thread-safe counting stub for <see cref="IWormholeAdapter"/>.
///
/// Used by <see cref="BridgeSafetyHardeningIntegrationTests.ConcurrentRedeem_SameVAAReadyBridge_ExactlyOneOnChainCallAndCompletedRow"/>
/// to make the "on-chain effect" observable without a real Guardian network.
/// Moq is not available in this project — hand-written stub, same precedent as
/// <c>CountingWormholeAdapter</c> in G2.
/// </summary>
internal sealed class BridgeSafetyCountingWormholeAdapter : IWormholeAdapter
{
    private int _redeemCallCount;

    public int RedeemCallCount => _redeemCallCount;

    public Task<AZOAResult<WormholeRedemptionResult>> RedeemTransferAsync(
        string targetChain, WormholeVAA vaa, string recipientAddress,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _redeemCallCount);
        return Task.FromResult(new AZOAResult<WormholeRedemptionResult>
        {
            IsError = false,
            Result  = new WormholeRedemptionResult { TxHash = "bsh_redeem_tx", Success = true }
        });
    }

    public Task<AZOAResult<WormholeTransferInitiation>> InitiateTransferAsync(
        string sourceChain, string targetChain, string tokenId,
        string senderAddress, string recipientAddress, int amount,
        CancellationToken ct = default)
        => Task.FromResult(new AZOAResult<WormholeTransferInitiation>
        {
            IsError = true,
            Message = "BridgeSafetyCountingWormholeAdapter: InitiateTransfer not stubbed"
        });

    public Task<AZOAResult<WormholeVAA>> FetchVAAAsync(
        int emitterChainId, string emitterAddress, long sequence,
        CancellationToken ct = default)
        => Task.FromResult(new AZOAResult<WormholeVAA>
        {
            IsError = true,
            Message = "BridgeSafetyCountingWormholeAdapter: FetchVAA not stubbed"
        });

    public Task<AZOAResult<bool>> VerifyVAAAsync(WormholeVAA vaa, CancellationToken ct = default)
        => Task.FromResult(new AZOAResult<bool> { IsError = false, Result = true });

    public int? GetWormholeChainId(string azoaChainName) => null;

    public bool IsRouteSupported(string sourceChain, string targetChain) => true;
}
