using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Connection;
using Azoa.SurrealDb.Client.Query;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Bridge;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores.Surreal;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// Integration tests for <see cref="SurrealBridgeStore"/> against a live
/// SurrealDB container. Each test instance gets its own SurrealDB namespace
/// (inherited from <see cref="IntegrationTestBase"/>) so concurrent runs do
/// not contend.
///
/// <para>
/// Skipping behaviour: when the SurrealDB test container is not running these
/// tests no-op via an early <see cref="SkipIfSurrealDbUnavailableAsync"/>
/// guard. The integration-tests project does not depend on
/// <c>Xunit.SkippableFact</c>, so we use a returns-early pattern (the same
/// idiom <see cref="IntegrationTestBase"/> uses across the harness). On a CI
/// agent with the container running every test exercises the full SurrealQL
/// path end-to-end.
/// </para>
///
/// <para>
/// Schema dependency: each test relies on
/// <see cref="IntegrationTestBase.InitializeAsync"/> applying the
/// <c>Persistence/SurrealDb/Schemas/*.surql</c> files via the SurrealDB HTTP
/// API. That harness wiring is owned by the wave-1 base class — these tests
/// do not redefine tables.
/// </para>
/// </summary>
public class SurrealBridgeStoreTests : IntegrationTestBase
{
    public SurrealBridgeStoreTests(AZOATestWebApplicationFactory factory)
        : base(factory)
    {
    }

    // ── AddBridge + GetBridge round-trip ──────────────────────────────────────

    [SkippableFact]
    public async Task AddBridge_GetBridge_RoundTripsAllFields()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable at http://localhost:8442 — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");
        var store = await CreateStoreAsync();

        var bridge = NewBridge(id: "br_rt_001");
        await store.AddBridgeAsync(bridge);

        var loaded = await store.GetBridgeAsync(bridge.Id);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(bridge.Id);
        loaded.AvatarId.Should().Be(bridge.AvatarId);
        loaded.SourceChain.Should().Be(bridge.SourceChain);
        loaded.TargetChain.Should().Be(bridge.TargetChain);
        loaded.SourceTokenId.Should().Be(bridge.SourceTokenId);
        loaded.SourceAddress.Should().Be(bridge.SourceAddress);
        loaded.TargetAddress.Should().Be(bridge.TargetAddress);
        loaded.Amount.Should().Be(bridge.Amount);
        loaded.Status.Should().Be(bridge.Status);
        loaded.Mode.Should().Be(bridge.Mode);
        loaded.CreatedAt.Should().BeCloseTo(bridge.CreatedAt, TimeSpan.FromSeconds(2));
    }

    [SkippableFact]
    public async Task GetBridge_NonExistent_ReturnsNull()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable at http://localhost:8442 — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");
        var store = await CreateStoreAsync();

        var loaded = await store.GetBridgeAsync("br_missing_xyz");

        loaded.Should().BeNull();
    }

    // ── GetBridgeHistory filters by avatar + orders by CreatedAt ──────────────

    [SkippableFact]
    public async Task GetBridgeHistory_FiltersByAvatarAndOrdersAscending()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable at http://localhost:8442 — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");
        var store = await CreateStoreAsync();

        var avatarA = Guid.NewGuid();
        var avatarB = Guid.NewGuid();
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        await store.AddBridgeAsync(NewBridge("br_h_a2", avatarA, createdAt: baseTime.AddMinutes(2)));
        await store.AddBridgeAsync(NewBridge("br_h_a1", avatarA, createdAt: baseTime.AddMinutes(1)));
        await store.AddBridgeAsync(NewBridge("br_h_b1", avatarB, createdAt: baseTime.AddMinutes(3)));

        var historyA = await store.GetBridgeHistoryAsync(avatarA);

        historyA.Should().HaveCount(2);
        historyA.Select(b => b.Id).Should().ContainInOrder("br_h_a1", "br_h_a2");
        historyA.Should().OnlyContain(b => b.AvatarId == avatarA);
    }

    // ── GetNonTerminalBridgeIds: status + stale + batch + order ───────────────

    [SkippableFact]
    public async Task GetNonTerminalBridgeIds_FiltersByStatusStaleAndOrdersAscWithBatch()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable at http://localhost:8442 — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");
        var store = await CreateStoreAsync();

        var staleBefore = DateTime.UtcNow.AddMinutes(-1);
        var oldEnough   = DateTime.UtcNow.AddMinutes(-30);
        var freshlyMade = DateTime.UtcNow;

        // Three stale non-terminal rows (eligible).
        await store.AddBridgeAsync(NewBridge("br_s_1", createdAt: oldEnough.AddMinutes(-2), status: BridgeStatus.Initiated));
        await store.AddBridgeAsync(NewBridge("br_s_2", createdAt: oldEnough.AddMinutes(-1), status: BridgeStatus.AwaitingVAA));
        await store.AddBridgeAsync(NewBridge("br_s_3", createdAt: oldEnough,                status: BridgeStatus.Redeeming));

        // One stale terminal (must be excluded).
        await store.AddBridgeAsync(NewBridge("br_term", createdAt: oldEnough, status: BridgeStatus.Completed));
        // One fresh non-terminal (must be excluded by stale filter).
        await store.AddBridgeAsync(NewBridge("br_fresh", createdAt: freshlyMade, status: BridgeStatus.Initiated));

        var nonTerminal = new[]
        {
            BridgeStatus.Initiated,
            BridgeStatus.Locked,
            BridgeStatus.AwaitingVAA,
            BridgeStatus.VAAReady,
            BridgeStatus.Redeeming,
            BridgeStatus.Reversing,
        };

        // Batch=2 of 3 eligible — returns the oldest two in ascending order.
        var ids = await store.GetNonTerminalBridgeIdsAsync(nonTerminal, staleBefore, batch: 2);

        ids.Should().HaveCount(2);
        ids.Should().ContainInOrder("br_s_1", "br_s_2");
    }

    [SkippableFact]
    public async Task GetNonTerminalBridgeIds_EmptyStatusSet_ReturnsEmpty()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable at http://localhost:8442 — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");
        var store = await CreateStoreAsync();

        var ids = await store.GetNonTerminalBridgeIdsAsync(
            Array.Empty<BridgeStatus>(), DateTime.UtcNow, batch: 100);

        ids.Should().BeEmpty();
    }

    // ── TryTransitionBridgeStatus: success ────────────────────────────────────

    [SkippableFact]
    public async Task TryTransitionBridgeStatus_WhenExpectedMatches_AffectsOne()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable at http://localhost:8442 — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");
        var store = await CreateStoreAsync();

        var bridge = NewBridge("br_tx_ok", status: BridgeStatus.Initiated);
        await store.AddBridgeAsync(bridge);

        var affected = await store.TryTransitionBridgeStatusAsync(
            bridge.Id, BridgeStatus.Initiated, BridgeStatus.Locked, alsoSet: null);

        affected.Should().Be(1);
        var reloaded = await store.GetBridgeAsync(bridge.Id);
        reloaded!.Status.Should().Be(BridgeStatus.Locked);
    }

    // ── TryTransitionBridgeStatus: lost-race ──────────────────────────────────

    [SkippableFact]
    public async Task TryTransitionBridgeStatus_WhenExpectedMismatch_AffectsZeroAndLeavesStateUntouched()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable at http://localhost:8442 — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");
        var store = await CreateStoreAsync();

        var bridge = NewBridge("br_tx_race", status: BridgeStatus.Locked);
        await store.AddBridgeAsync(bridge);

        // Caller thinks the bridge is still Initiated — it is actually Locked.
        var affected = await store.TryTransitionBridgeStatusAsync(
            bridge.Id, BridgeStatus.Initiated, BridgeStatus.Completed, alsoSet: null);

        affected.Should().Be(0);
        var reloaded = await store.GetBridgeAsync(bridge.Id);
        reloaded!.Status.Should().Be(BridgeStatus.Locked); // state untouched
    }

    // ── TryTransitionBridgeStatus: alsoSet writes additional fields atomically ─

    [SkippableFact]
    public async Task TryTransitionBridgeStatus_AlsoSet_WritesAdditionalFieldsAtomically()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable at http://localhost:8442 — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");
        var store = await CreateStoreAsync();

        var bridge = NewBridge("br_tx_set", status: BridgeStatus.Locked);
        await store.AddBridgeAsync(bridge);

        var mutation = new BridgeStatusMutation
        {
            LockTxHash             = "0xabc123def",
            MintTxHash             = "0xfeed1234",
            ErrorMessage           = null,                 // null fields stay untouched
            SetCompletedAtUtcNow   = true,
            IdempotencyKey         = "idem-001",
            TargetTokenId          = "ASA:777"
        };

        var affected = await store.TryTransitionBridgeStatusAsync(
            bridge.Id, BridgeStatus.Locked, BridgeStatus.Completed, mutation);

        affected.Should().Be(1);
        var reloaded = await store.GetBridgeAsync(bridge.Id);
        reloaded!.Status.Should().Be(BridgeStatus.Completed);
        reloaded.LockTxHash.Should().Be("0xabc123def");
        reloaded.MintTxHash.Should().Be("0xfeed1234");
        reloaded.IdempotencyKey.Should().Be("idem-001");
        reloaded.TargetTokenId.Should().Be("ASA:777");
        reloaded.CompletedAt.Should().NotBeNull();
        reloaded.CompletedAt!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    // ── SaveVaaFetchResult: all four fields land + status transitions ─────────

    [SkippableFact]
    public async Task SaveVaaFetchResult_WritesAllFieldsAndTransitionsStatus()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable at http://localhost:8442 — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");
        var store = await CreateStoreAsync();

        var bridge = NewBridge("br_vaa", status: BridgeStatus.AwaitingVAA);
        await store.AddBridgeAsync(bridge);

        await store.SaveVaaFetchResultAsync(
            id: bridge.Id,
            vaaBytes: "vaa-bytes-payload",
            sigCount: 13,
            proofData: "proof-blob",
            statusVAAReady: BridgeStatus.VAAReady);

        var reloaded = await store.GetBridgeAsync(bridge.Id);
        reloaded!.VaaBytes.Should().Be("vaa-bytes-payload");
        reloaded.VaaSignatureCount.Should().Be(13);
        reloaded.ProofData.Should().Be("proof-blob");
        reloaded.Status.Should().Be(BridgeStatus.VAAReady);
    }

    // ── TryInsertConsumedVaa: first/second insert ─────────────────────────────

    [SkippableFact]
    public async Task TryInsertConsumedVaa_FirstInsertSucceedsDuplicateReturnsFalse()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable at http://localhost:8442 — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");
        var store = await CreateStoreAsync();

        var record = new ConsumedVaaRecord
        {
            Digest             = MakeDigest("vaa_a"),
            EmitterChainId     = 2,
            EmitterAddress     = MakeEmitterAddress("emit_a"),
            Sequence           = 100,
            BridgeTransactionId = "br_consume_a",
            ConsumedAt         = DateTime.UtcNow,
        };

        var first  = await store.TryInsertConsumedVaaAsync(record);
        var second = await store.TryInsertConsumedVaaAsync(record);

        first.Should().BeTrue("first insert is a new digest");
        second.Should().BeFalse("UNIQUE(digest) collision must be reported as false, not a thrown exception");
    }

    // ── TryInsertConsumedVaa: triple-collision returns false (no exception leak) ─

    [SkippableFact]
    public async Task TryInsertConsumedVaa_DuplicateByEmitterTripleReturnsFalse()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable at http://localhost:8442 — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");
        var store = await CreateStoreAsync();

        var original = new ConsumedVaaRecord
        {
            Digest             = MakeDigest("vaa_orig"),
            EmitterChainId     = 5,
            EmitterAddress     = MakeEmitterAddress("emit_dup"),
            Sequence           = 42,
            BridgeTransactionId = "br_orig",
            ConsumedAt         = DateTime.UtcNow,
        };
        var sameTripleDifferentDigest = new ConsumedVaaRecord
        {
            Digest             = MakeDigest("vaa_other"),     // distinct digest
            EmitterChainId     = 5,
            EmitterAddress     = MakeEmitterAddress("emit_dup"),
            Sequence           = 42,                          // same emitter triple
            BridgeTransactionId = "br_other",
            ConsumedAt         = DateTime.UtcNow,
        };

        var first  = await store.TryInsertConsumedVaaAsync(original);
        var second = await store.TryInsertConsumedVaaAsync(sameTripleDifferentDigest);

        first.Should().BeTrue();
        second.Should().BeFalse("UNIQUE(emitter_chain_id,emitter_address,sequence) collision must be reported as false");
    }

    // ── GetOperation + TryTransitionOperationStatus ───────────────────────────

    [SkippableFact]
    public async Task GetOperation_RoundTripsAndConditionalTransitionRespectsExpected()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable at http://localhost:8442 — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");
        var store = await CreateStoreAsync();

        // Seed an operation row directly via the executor (no IBlockchainOperationStore
        // dependency in this test — we exercise the IBridgeStore.GetOperation /
        // TryTransitionOperationStatus parallel surface).
        var executor = await CreateExecutorAsync();
        var opId = Guid.NewGuid();
        var surrealOpId = opId.ToString("N").ToLowerInvariant();

        var seed = SurrealQuery
            .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
            .WithParam("_t", "operation_log")
            .WithParam("_id", surrealOpId)
            .WithParam("_body", new
            {
                id             = surrealOpId,
                operation_type = "Mint",
                status         = OperationStatus.Pending,
                created_date   = DateTimeOffset.UtcNow,
            });
        (await executor.ExecuteAsync(seed)).EnsureAllOk();

        var loaded = await store.GetOperationAsync(opId);
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(opId);
        loaded.Status.Should().Be(OperationStatus.Pending);

        var completedAt = DateTime.UtcNow;
        var winner = await store.TryTransitionOperationStatusAsync(
            opId, OperationStatus.Pending, OperationStatus.Completed, completedAt);
        winner.Should().Be(1);

        var loser = await store.TryTransitionOperationStatusAsync(
            opId, OperationStatus.Pending, OperationStatus.Failed, completedDate: null);
        loser.Should().Be(0, "Status is already Completed; the lost-race contract returns 0 verbatim.");

        var after = await store.GetOperationAsync(opId);
        after!.Status.Should().Be(OperationStatus.Completed);
        after.CompletedDate.Should().NotBeNull();
    }

    // ── GetNonTerminalOperationIds ────────────────────────────────────────────

    [Fact]
    public async Task GetNonTerminalOperationIds_FiltersByStatusStaleAndBatch()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable at http://localhost:8442 — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");
        var store = await CreateStoreAsync();
        var executor = await CreateExecutorAsync();

        var staleBefore = DateTime.UtcNow.AddMinutes(-1);

        var pendingOld  = await SeedOperationAsync(executor, OperationStatus.Pending,           DateTime.UtcNow.AddMinutes(-30));
        var pendingMid  = await SeedOperationAsync(executor, OperationStatus.AwaitingSignature, DateTime.UtcNow.AddMinutes(-20));
        var completed   = await SeedOperationAsync(executor, OperationStatus.Completed,         DateTime.UtcNow.AddMinutes(-30));
        var pendingFresh= await SeedOperationAsync(executor, OperationStatus.Pending,           DateTime.UtcNow);

        var nonTerminal = new[] { OperationStatus.Pending, OperationStatus.AwaitingSignature };
        var ids = await store.GetNonTerminalOperationIdsAsync(nonTerminal, staleBefore, batch: 50);

        ids.Should().Contain(new[] { pendingOld, pendingMid });
        ids.Should().NotContain(completed);
        ids.Should().NotContain(pendingFresh);
    }

    // ── Local helpers ─────────────────────────────────────────────────────────

    private static BridgeTransactionResult NewBridge(
        string id,
        Guid? avatarId = null,
        DateTime? createdAt = null,
        BridgeStatus status = BridgeStatus.Initiated)
    {
        return new BridgeTransactionResult
        {
            Id            = id,
            AvatarId      = avatarId ?? Guid.NewGuid(),
            SourceChain   = "Algorand",
            TargetChain   = "Solana",
            SourceTokenId = "ASA:123",
            TargetTokenId = null,
            // Each row must have a unique SourceAddress + Lock route to avoid
            // colliding on bridge_tx_lock_route (NULL lock_tx_hash collides
            // only when explicitly set — pre-lock NULLs do not collide).
            SourceAddress = "SRC_" + id,
            TargetAddress = "TGT_" + id,
            Amount        = 1_000,
            Status        = status,
            Mode          = BridgeMode.Trusted,
            CreatedAt     = createdAt ?? DateTime.UtcNow,
            IdempotencyKey = "idem_" + id,
        };
    }

    /// <summary>Build a deterministic 128-char keccak-shaped hex digest from a seed.</summary>
    private static string MakeDigest(string seed)
    {
        var sb = new System.Text.StringBuilder(128);
        // Hash twice for 64 bytes of hex characters.
        var data = System.Security.Cryptography.SHA512.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        foreach (var b in data) sb.Append(b.ToString("x2"));
        // SHA512 emits 128 hex chars exactly.
        return sb.ToString();
    }

    /// <summary>Canonical Wormhole 32-byte emitter address: 64 lowercase hex chars.</summary>
    private static string MakeEmitterAddress(string seed)
    {
        var data = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        var sb = new System.Text.StringBuilder(64);
        foreach (var b in data) sb.Append(b.ToString("x2"));
        return sb.ToString(); // SHA256 = 32 bytes = 64 hex chars
    }

    private async Task<Guid> SeedOperationAsync(ISurrealExecutor executor, string status, DateTime createdDate)
    {
        var id = Guid.NewGuid();
        var surrealId = id.ToString("N").ToLowerInvariant();
        var q = SurrealQuery
            .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
            .WithParam("_t", "operation_log")
            .WithParam("_id", surrealId)
            .WithParam("_body", new
            {
                id             = surrealId,
                operation_type = "Mint",
                status         = status,
                created_date   = new DateTimeOffset(DateTime.SpecifyKind(createdDate, DateTimeKind.Utc), TimeSpan.Zero),
            });
        (await executor.ExecuteAsync(q)).EnsureAllOk();
        return id;
    }

    private async Task<SurrealBridgeStore> CreateStoreAsync()
    {
        var executor = await CreateExecutorAsync();
        return new SurrealBridgeStore(executor);
    }

    private async Task<ISurrealExecutor> CreateExecutorAsync()
    {
        // The test-host DI graph wires a default-namespace executor, but each
        // test instance owns a unique TestNamespace (set by IntegrationTestBase).
        // Build a connection bound to *that* namespace so reads/writes land
        // inside the isolated test slice rather than the shared "azoa" NS.
        var options = new SurrealConnectionOptions
        {
            Endpoint   = SurrealTestDefaults.Endpoint,
            Namespace  = TestNamespace,
            Database   = "test",
            User       = SurrealTestDefaults.User,
            Password   = SurrealTestDefaults.Password,
        };

        var http = new HttpClient();
        var connection = new HttpSurrealConnection(http, options);
        var executor = new DefaultSurrealExecutor(connection);
        return await Task.FromResult<ISurrealExecutor>(executor);
    }

    // SkipIfSurrealDbUnavailableAsync was promoted to IntegrationTestBase on
    // 2026-05-22 (CLOSEOUT Stream E) so the five pre-cutover gate tests can
    // consume one canonical probe. The base-class protected method is inherited.
}
