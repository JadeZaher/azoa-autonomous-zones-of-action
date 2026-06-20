using FluentAssertions;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Connection;
using Oasis.SurrealDb.Client.Query;
using OASIS.WebAPI.IntegrationTests.Builders;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Providers.Stores.Surreal;

namespace OASIS.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// Store-layer integration tests for <see cref="SurrealBlockchainOperationStore"/>.
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
public sealed class SurrealBlockchainOperationStoreTests : IAsyncLifetime
{
    // ── Connection config ─────────────────────────────────────────────────────

    // Connection config sourced from SurrealTestDefaults (points at local instance).

    // ── Per-instance state ────────────────────────────────────────────────────

    private readonly string _testNamespace = $"test{Guid.NewGuid():N}";
    private SurrealBlockchainOperationStore _store = null!;
    private HttpSurrealConnection _connection = null!;
    private HttpClient _httpClient = null!;
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

        _httpClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        _connection = new HttpSurrealConnection(_httpClient, options);
        var executor = new DefaultSurrealExecutor(_connection);
        _store = new SurrealBlockchainOperationStore(executor);

        // Bootstrap test namespace + operation_log schema.
        await BootstrapSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (!_surrealAvailable || _connection is null) return;
        try
        {
            // Best-effort teardown — drop the test namespace.
            await DropNamespaceAsync();
        }
        catch
        {
            // Swallow; teardown must never pollute test results.
        }
        finally
        {
            _connection?.Dispose();
            _httpClient?.Dispose();
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Test 1: Upsert creates a record; GetById retrieves it with identical field values.
    /// </summary>
    [SkippableFact]
    public async Task Upsert_ThenGetById_RoundTrips()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var op = new BlockchainOperationBuilder()
            .WithId(Guid.NewGuid())
            .ForAvatar(Guid.NewGuid())
            .OfType("Mint")
            .WithStatus(OperationStatus.Pending)
            .Build();

        var upsertResult = await _store.UpsertAsync(op);

        upsertResult.IsError.Should().BeFalse();
        upsertResult.Message.Should().Be("Saved.");

        var getResult = await _store.GetByIdAsync(op.Id);

        getResult.IsError.Should().BeFalse();
        getResult.Message.Should().Be("Success");
        getResult.Result.Should().NotBeNull();
        getResult.Result!.Id.Should().Be(op.Id);
        getResult.Result.AvatarId.Should().Be(op.AvatarId);
        getResult.Result.OperationType.Should().Be("Mint");
        getResult.Result.Status.Should().Be(OperationStatus.Pending);
    }

    /// <summary>
    /// Test 2: GetById for a non-existent id returns IsError=true with "Operation not found."
    /// </summary>
    [SkippableFact]
    public async Task GetById_NotFound_ReturnsError()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var result = await _store.GetByIdAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("Operation not found.");
        result.Result.Should().BeNull();
    }

    /// <summary>
    /// Test 3: GetByAvatar returns only operations belonging to that avatar.
    /// </summary>
    [SkippableFact]
    public async Task GetByAvatar_FiltersCorrectly()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var targetAvatar = Guid.NewGuid();
        var otherAvatar  = Guid.NewGuid();

        var op1 = new BlockchainOperationBuilder().WithId(Guid.NewGuid()).ForAvatar(targetAvatar).Build();
        var op2 = new BlockchainOperationBuilder().WithId(Guid.NewGuid()).ForAvatar(targetAvatar).Build();
        var op3 = new BlockchainOperationBuilder().WithId(Guid.NewGuid()).ForAvatar(otherAvatar).Build();

        await _store.UpsertAsync(op1);
        await _store.UpsertAsync(op2);
        await _store.UpsertAsync(op3);

        var result = await _store.GetByAvatarAsync(targetAvatar);

        result.IsError.Should().BeFalse();
        result.Message.Should().Be("Success");
        var list = result.Result!.ToList();
        list.Should().HaveCount(2);
        list.All(o => o.AvatarId == targetAvatar).Should().BeTrue();
    }

    /// <summary>
    /// Test 4: Delete removes the record; subsequent GetById returns not-found.
    /// </summary>
    [SkippableFact]
    public async Task Delete_RemovesRecord()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var op = new BlockchainOperationBuilder().WithId(Guid.NewGuid()).Build();
        await _store.UpsertAsync(op);

        var deleteResult = await _store.DeleteAsync(op.Id);

        deleteResult.IsError.Should().BeFalse();
        deleteResult.Result.Should().BeTrue();
        deleteResult.Message.Should().Be("Deleted.");

        var getResult = await _store.GetByIdAsync(op.Id);
        getResult.IsError.Should().BeTrue();
        getResult.Message.Should().Be("Operation not found.");
    }

    /// <summary>
    /// Test 5: Delete of a non-existent id returns IsError=true.
    /// </summary>
    [SkippableFact]
    public async Task Delete_NotFound_ReturnsError()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var result = await _store.DeleteAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Result.Should().BeFalse();
        result.Message.Should().Be("Operation not found.");
    }

    /// <summary>
    /// Test 6: Upsert on an existing id overwrites the record (update path).
    /// </summary>
    [SkippableFact]
    public async Task Upsert_UpdatePath_OverwritesRecord()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var id = Guid.NewGuid();
        var original = new BlockchainOperationBuilder()
            .WithId(id)
            .OfType("Mint")
            .WithStatus(OperationStatus.Pending)
            .Build();

        await _store.UpsertAsync(original);

        // Overwrite with a new status and operation type.
        var updated = new BlockchainOperationBuilder()
            .WithId(id)
            .OfType("Transfer")
            .WithStatus(OperationStatus.Completed)
            .Build();

        var upsertResult = await _store.UpsertAsync(updated);

        upsertResult.IsError.Should().BeFalse();
        upsertResult.Message.Should().Be("Saved.");

        var getResult = await _store.GetByIdAsync(id);
        getResult.Result!.OperationType.Should().Be("Transfer");
        getResult.Result.Status.Should().Be(OperationStatus.Completed);
    }

    /// <summary>
    /// Test 7: G2 TryTransitionStatusAsync — winner succeeds, concurrent loser is rejected.
    ///
    /// Proves the G2 UpdateOnly contract: when two callers race to transition the same
    /// operation from Pending to Completed, exactly one wins (returns true) and the other
    /// finds the state already changed (returns false with 0 affected rows).
    /// </summary>
    [SkippableFact]
    public async Task TryTransitionStatus_G2_ExactlyOneCallerWins()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var id = Guid.NewGuid();
        var op = new BlockchainOperationBuilder()
            .WithId(id)
            .WithStatus(OperationStatus.Pending)
            .Build();

        await _store.UpsertAsync(op);

        // Both callers attempt to claim Pending → Completed simultaneously.
        var winnerTask = _store.TryTransitionStatusAsync(id, OperationStatus.Pending, OperationStatus.Completed);
        var loserTask  = _store.TryTransitionStatusAsync(id, OperationStatus.Pending, OperationStatus.Completed);

        var results = await Task.WhenAll(winnerTask, loserTask);

        // Exactly one must have won.
        int wins = results.Count(r => r);
        wins.Should().Be(1, "exactly one concurrent caller should win the G2 race");

        // Post-condition: the record is in Completed state.
        var finalState = await _store.GetByIdAsync(id);
        finalState.Result!.Status.Should().Be(OperationStatus.Completed);
    }

    /// <summary>
    /// Test 8: TryTransitionStatusAsync with wrong expected status returns false without mutation.
    /// </summary>
    [SkippableFact]
    public async Task TryTransitionStatus_WrongExpectedState_ReturnsFalseWithoutMutation()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var id = Guid.NewGuid();
        var op = new BlockchainOperationBuilder()
            .WithId(id)
            .WithStatus(OperationStatus.Completed)
            .Build();

        await _store.UpsertAsync(op);

        // Attempt transition from Pending (wrong — actual state is Completed).
        var result = await _store.TryTransitionStatusAsync(id, OperationStatus.Pending, OperationStatus.Failed);

        result.Should().BeFalse("the WHERE clause did not match so no row was updated");

        // State must be unchanged.
        var getResult = await _store.GetByIdAsync(id);
        getResult.Result!.Status.Should().Be(OperationStatus.Completed);
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

    /// <summary>
    /// Applies a minimal inline DDL for operation_log so tests do not depend on
    /// the schema runner having been executed separately. The SCHEMAFULL definition
    /// matches the wave-1 schema (050_operation_log.mermaid).
    /// </summary>
    private async Task BootstrapSchemaAsync()
    {
        // Use the SurrealDB HTTP SQL endpoint directly for DDL.
        using var ddlClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));
        ddlClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        // SurrealDB 3.x requires "Surreal-NS"/"Surreal-DB" headers; scope the
        // DDL client to the per-test namespace so the table DDL lands there.
        ddlClient.DefaultRequestHeaders.Add("Surreal-NS", _testNamespace);
        ddlClient.DefaultRequestHeaders.Add("Surreal-DB", "test");

        // The namespace/database identifiers cannot be $params in DDL. Create
        // the namespace at ROOT, then the database scoped to the namespace with
        // the NS header ONLY (a Surreal-DB header naming a not-yet-existing db
        // makes the connection-level USE fail). The table DDL below then runs
        // with both NS+DB headers set.
        using (var nsClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) })
        {
            nsClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
            await nsClient.PostAsync("/sql", new StringContent(
                $"DEFINE NAMESPACE IF NOT EXISTS {_testNamespace}", System.Text.Encoding.UTF8, "text/plain"));
            nsClient.DefaultRequestHeaders.Add("Surreal-NS", _testNamespace);
            await nsClient.PostAsync("/sql", new StringContent(
                "DEFINE DATABASE IF NOT EXISTS test", System.Text.Encoding.UTF8, "text/plain"));
        }

        const string ddl = """
            DEFINE TABLE IF NOT EXISTS operation_log SCHEMAFULL;
            DEFINE FIELD IF NOT EXISTS id             ON operation_log TYPE string;
            DEFINE FIELD IF NOT EXISTS avatar_id      ON operation_log TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS wallet_id      ON operation_log TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS operation_type ON operation_log TYPE string;
            DEFINE FIELD IF NOT EXISTS status         ON operation_log TYPE string DEFAULT 'Pending';
            DEFINE FIELD IF NOT EXISTS parameters     ON operation_log TYPE option<object>;
            DEFINE FIELD IF NOT EXISTS token_uri      ON operation_log TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS amount         ON operation_log TYPE option<int>;
            DEFINE FIELD IF NOT EXISTS asset_type     ON operation_log TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS source_holon_id ON operation_log TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS target_holon_id ON operation_log TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS exchange_rate  ON operation_log TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS recipient_address ON operation_log TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS idempotency_key ON operation_log TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS error          ON operation_log TYPE option<string>;
            DEFINE FIELD IF NOT EXISTS created_date   ON operation_log TYPE datetime;
            DEFINE FIELD IF NOT EXISTS completed_date ON operation_log TYPE option<datetime>
            """;

        // Raw text/plain DDL — namespace is passed via header, not interpolated.
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
                var removeSql = $"REMOVE NAMESPACE IF EXISTS {_testNamespace}";
        var content = new StringContent(removeSql, System.Text.Encoding.UTF8, "text/plain");
        await dropClient.PostAsync("/sql", content);
    }
}
