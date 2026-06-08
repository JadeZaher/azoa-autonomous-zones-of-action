using FluentAssertions;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Connection;
using Oasis.SurrealDb.Client.Query;
using OASIS.WebAPI.Core.Idempotency;
using OASIS.WebAPI.Models.Idempotency;

namespace OASIS.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// Store-layer integration tests for <see cref="SurrealIdempotencyStore"/>.
///
/// Each test creates an isolated SurrealDB namespace (test_{guid}) against the
/// test container on port 8442. Tests skip gracefully via <see cref="SkippableFact"/>
/// and <see cref="Skip.IfNot"/> when the SurrealDB container is unavailable, so the
/// test runner reports them as Skipped instead of Passed.
///
/// Namespace isolation: each test instance gets its own SurrealDB namespace so
/// tests can run in parallel without data leakage. Teardown drops the namespace
/// via <see cref="IAsyncLifetime.DisposeAsync"/>.
/// </summary>
public sealed class SurrealIdempotencyStoreTests : IAsyncLifetime
{
    // ── Connection config ──────────────────────────────────────────────────────

    // Connection config sourced from SurrealTestDefaults (points at local instance).

    // ── Per-instance state ─────────────────────────────────────────────────────

    private readonly string _testNamespace = $"test{Guid.NewGuid():N}";
    private SurrealIdempotencyStore _store = null!;
    private HttpSurrealConnection _connection = null!;
    private bool _surrealAvailable;

    // ── IAsyncLifetime ─────────────────────────────────────────────────────────

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
        var executor = new DefaultSurrealExecutor(_connection);
        _store = new SurrealIdempotencyStore(executor);

        await BootstrapSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (!_surrealAvailable || _connection is null) return;
        try
        {
            await DropNamespaceAsync();
        }
        catch
        {
            // Best-effort — swallow teardown errors.
        }
        finally
        {
            _connection.Dispose();
        }
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Test 1: TryClaimAsync first time → wins (Won=true, state=InProgress).
    /// </summary>
    [SkippableFact]
    public async Task TryClaim_FirstTime_Wins()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var key = UniqueKey();

        var claim = await _store.TryClaimAsync(key, "faucet_dispense", CancellationToken.None);

        claim.Won.Should().BeTrue("first caller must win the claim");
        claim.Record.Key.Should().Be(key);
        claim.Record.State.Should().Be(IdempotencyState.InProgress);
        claim.Record.OperationType.Should().Be("faucet_dispense");
    }

    /// <summary>
    /// Test 2: TryClaimAsync second time same key → loses, returns existing record.
    /// </summary>
    [SkippableFact]
    public async Task TryClaim_SecondTime_SameKey_Loses()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var key = UniqueKey();

        var first  = await _store.TryClaimAsync(key, "bridge_redeem", CancellationToken.None);
        var second = await _store.TryClaimAsync(key, "bridge_redeem", CancellationToken.None);

        first.Won.Should().BeTrue();
        second.Won.Should().BeFalse("duplicate caller must lose");
        second.Record.Key.Should().Be(key);
        second.Record.State.Should().Be(IdempotencyState.InProgress,
            "existing InProgress record should be returned");
    }

    /// <summary>
    /// Test 3: Concurrent callers — exactly one wins.
    ///
    /// Uses Task.WhenAll with N callers attempting the same key concurrently.
    /// Exactly one must return Won=true; the rest must return Won=false.
    /// </summary>
    [SkippableFact]
    public async Task TryClaim_Concurrent_ExactlyOneWins()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        const int concurrency = 6;
        var key = UniqueKey();

        var tasks = Enumerable
            .Range(0, concurrency)
            .Select(_ => _store.TryClaimAsync(key, "concurrent_op", CancellationToken.None))
            .ToArray();

        var claims = await Task.WhenAll(tasks);

        int wins = claims.Count(c => c.Won);
        wins.Should().Be(1, "exactly one concurrent caller should win the INSERT race");

        int losses = claims.Count(c => !c.Won);
        losses.Should().Be(concurrency - 1, "all other callers should lose and see Won=false");
    }

    /// <summary>
    /// Test 4: CompleteAsync from InProgress → state=Completed, payload stored.
    /// </summary>
    [SkippableFact]
    public async Task Complete_FromInProgress_SetsCompletedWithPayload()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var key     = UniqueKey();
        var payload = """{"txId":"abc123","amount":"1.5"}""";

        await _store.TryClaimAsync(key, "mint", CancellationToken.None);
        await _store.CompleteAsync(key, payload, CancellationToken.None);

        var record = await _store.GetAsync(key, CancellationToken.None);

        record.Should().NotBeNull();
        record!.State.Should().Be(IdempotencyState.Completed);
        record.ResultPayload.Should().Be(payload);
    }

    /// <summary>
    /// Test 5: CompleteAsync from non-InProgress (Failed) → no-op, state not overwritten.
    ///
    /// The conditional WHERE state = InProgress guard means the UPDATE affects zero
    /// rows when state is already Failed. The store must not throw and must leave the
    /// state unchanged.
    /// </summary>
    [SkippableFact]
    public async Task Complete_FromFailed_IsNoOp()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var key = UniqueKey();

        await _store.TryClaimAsync(key, "transfer", CancellationToken.None);
        await _store.FailAsync(key, "network timeout", CancellationToken.None);

        // Must not throw even though state is already Failed.
        var completeAction = async () =>
            await _store.CompleteAsync(key, "ignored_payload", CancellationToken.None);
        await completeAction.Should().NotThrowAsync(
            "CompleteAsync on a Failed claim must be a silent no-op");

        // State remains Failed.
        var record = await _store.GetAsync(key, CancellationToken.None);
        record!.State.Should().Be(IdempotencyState.Failed,
            "state must not be overwritten from Failed to Completed");
    }

    /// <summary>
    /// Test 6: FailAsync from InProgress → state=Failed, error stored.
    /// </summary>
    [SkippableFact]
    public async Task Fail_FromInProgress_SetsFailedWithError()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var key   = UniqueKey();
        var error = "on-chain submission rejected: insufficient fee";

        await _store.TryClaimAsync(key, "bridge_redeem", CancellationToken.None);
        await _store.FailAsync(key, error, CancellationToken.None);

        var record = await _store.GetAsync(key, CancellationToken.None);

        record.Should().NotBeNull();
        record!.State.Should().Be(IdempotencyState.Failed);
        record.Error.Should().Be(error);
    }

    /// <summary>
    /// Test 7: GetAsync on an unclaimed key returns null.
    /// </summary>
    [SkippableFact]
    public async Task Get_UnknownKey_ReturnsNull()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var result = await _store.GetAsync("nonexistent-key-" + Guid.NewGuid(), CancellationToken.None);

        result.Should().BeNull("unclaimed key must return null");
    }

    /// <summary>
    /// Test 8: CompleteAsync then GetAsync — result_payload roundtrips verbatim.
    /// </summary>
    [SkippableFact]
    public async Task Complete_ThenGet_PayloadRoundtripsVerbatim()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var key = UniqueKey();
        // Payload with unicode and escaped characters to verify verbatim storage.
        var payload = """{"status":"ok","data":{"id":42,"label":"Sürreal éà"}}""";

        await _store.TryClaimAsync(key, "nft_mint", CancellationToken.None);
        await _store.CompleteAsync(key, payload, CancellationToken.None);

        var record = await _store.GetAsync(key, CancellationToken.None);

        record.Should().NotBeNull();
        record!.State.Should().Be(IdempotencyState.Completed);
        record.ResultPayload.Should().Be(payload,
            "result_payload must be stored and retrieved verbatim");
    }

    /// <summary>
    /// Test 9: DeterministicId encoding — same key always produces the same id,
    /// different keys produce different ids (basic collision-resistance check).
    /// </summary>
    [Fact]
    public void DeterministicId_SameKey_ProducesSameId()
    {
        var id1 = SurrealIdempotencyStore.DeterministicId("my-idempotency-key");
        var id2 = SurrealIdempotencyStore.DeterministicId("my-idempotency-key");
        var id3 = SurrealIdempotencyStore.DeterministicId("different-key");

        id1.Should().Be(id2, "deterministic id must be stable across calls");
        id1.Should().NotBe(id3, "different keys must produce different ids");
        id1.Should().MatchRegex("^[0-9a-f]{64}$",
            "deterministic id must be 64 lowercase hex chars (SHA-256)");
    }

    /// <summary>
    /// Test 10: FailAsync from non-InProgress (already Completed) → no-op.
    /// </summary>
    [SkippableFact]
    public async Task Fail_FromCompleted_IsNoOp()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var key = UniqueKey();

        await _store.TryClaimAsync(key, "exchange", CancellationToken.None);
        await _store.CompleteAsync(key, """{"ok":true}""", CancellationToken.None);

        var failAction = async () =>
            await _store.FailAsync(key, "should be ignored", CancellationToken.None);
        await failAction.Should().NotThrowAsync(
            "FailAsync on an already-Completed claim must be a silent no-op");

        var record = await _store.GetAsync(key, CancellationToken.None);
        record!.State.Should().Be(IdempotencyState.Completed,
            "state must not be overwritten from Completed to Failed");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string UniqueKey() => $"idem-test-{Guid.NewGuid():N}";

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

    /// <summary>
    /// Applies the minimal DDL for <c>idempotency_key_store</c> matching
    /// <c>Persistence/SurrealDb/Schemas/source/070_idempotency_key_store.mermaid</c>.
    /// The inline schema lets the tests be self-contained without requiring the
    /// schema runner to have been executed beforehand.
    /// </summary>
    private async Task BootstrapSchemaAsync()
    {
        using var ddlClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));
        ddlClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        ddlClient.DefaultRequestHeaders.Add("NS", _testNamespace);
        ddlClient.DefaultRequestHeaders.Add("DB", "test");

        // DDL mirrors 070_idempotency_key_store.mermaid.
        const string ddl = """
            DEFINE NAMESPACE IF NOT EXISTS $ns;
            USE NS $ns DB test;
            DEFINE DATABASE IF NOT EXISTS test;
            DEFINE TABLE IF NOT EXISTS idempotency_key_store SCHEMAFULL;
            DEFINE FIELD IF NOT EXISTS id             ON idempotency_key_store TYPE string ASSERT $value != NONE AND $value != "";
            DEFINE FIELD IF NOT EXISTS key            ON idempotency_key_store TYPE string ASSERT $value != NONE AND $value != "";
            DEFINE FIELD IF NOT EXISTS operation_type ON idempotency_key_store TYPE string ASSERT $value != NONE AND $value != "";
            DEFINE FIELD IF NOT EXISTS state          ON idempotency_key_store TYPE string DEFAULT "InProgress" ASSERT $value INSIDE ["InProgress", "Completed", "Failed"];
            DEFINE FIELD IF NOT EXISTS result_payload ON idempotency_key_store FLEXIBLE TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS error          ON idempotency_key_store FLEXIBLE TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS created_at     ON idempotency_key_store TYPE datetime;
            DEFINE FIELD IF NOT EXISTS updated_at     ON idempotency_key_store TYPE datetime;
            DEFINE FIELD IF NOT EXISTS ttl_expires_at ON idempotency_key_store FLEXIBLE TYPE option<datetime>;
            DEFINE INDEX IF NOT EXISTS idempotency_key_unique ON idempotency_key_store FIELDS key UNIQUE
            """;

        var content = new StringContent(ddl, System.Text.Encoding.UTF8, "text/plain");
        var response = await ddlClient.PostAsync("/sql", content);
        // Best-effort — tests may still work if table already exists.
        _ = response;
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
        var content = new StringContent(removeSql, System.Text.Encoding.UTF8, "text/plain");
        await dropClient.PostAsync("/sql", content);
    }
}
