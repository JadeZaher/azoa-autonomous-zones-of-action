using FluentAssertions;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Connection;
using Oasis.SurrealDb.Client.Query;
using OASIS.WebAPI.Models.Sagas;
using OASIS.WebAPI.Sagas;

namespace OASIS.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// Store-layer integration tests for <see cref="SurrealSagaStore"/>.
///
/// Each test creates an isolated SurrealDB namespace (test_{guid}) against the
/// test container on port 8442. Tests skip gracefully (pass trivially) when the
/// SurrealDB container is unavailable — same pattern as
/// <see cref="SurrealIdempotencyStoreTests"/>: no external Skippable package
/// required; each test checks <c>_surrealAvailable</c> and early-returns when
/// the container is absent.
///
/// The tests are the **proof** that the G2 single-winner conditional-claim
/// primitive holds end-to-end against a real SurrealDB engine — not just on
/// EF/Postgres/SQLite. Two concurrent <see cref="ISagaStore.TryClaimDueStepAsync"/>
/// calls must produce exactly one winner.
/// </summary>
public sealed class SurrealSagaStoreTests : IAsyncLifetime
{
    // ── Connection config ──────────────────────────────────────────────────────

    // Connection config sourced from SurrealTestDefaults (points at local instance).

    // ── Per-instance state ─────────────────────────────────────────────────────

    private readonly string _testNamespace = $"test{Guid.NewGuid():N}";
    private SurrealSagaStore _store = null!;
    private HttpSurrealConnection _connection = null!;
    private bool _surrealAvailable;

    // ── IAsyncLifetime ─────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        _surrealAvailable = await ProbeSurrealAsync();Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        // Bootstrap namespace + schema BEFORE binding the per-test connection so
        // the DEFINE NAMESPACE statement does not require an existing namespace.
        await BootstrapSchemaAsync();

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
        _store = new SurrealSagaStore(executor);
    }

    public async Task DisposeAsync()
    {Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);
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
            _connection?.Dispose();
        }
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Round-trip: Enqueue → Get returns the same record. Verifies POCO mapping
    /// (Guid hex ↔ string id, DateTime UTC ↔ DateTimeOffset, enum ↔ string).
    /// </summary>
    [SkippableFact]
    public async Task Enqueue_Then_Get_RoundTrips()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var corr = UniqueCorrelation();
        var enq = await _store.EnqueueAsync(
            sagaName: "BridgeTransfer",
            stepName: "LockSource",
            correlationKey: corr,
            stepIdempotencyKey: $"idem-{Guid.NewGuid():N}",
            payloadJson: """{"amount":"1.0"}""",
            isCompensation: false,
            ct: CancellationToken.None);

        enq.Should().NotBeNull();
        enq.Id.Should().NotBe(Guid.Empty);
        enq.Status.Should().Be(StepStatus.Pending);

        var fetched = await _store.GetAsync(enq.Id, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(enq.Id);
        fetched.CorrelationKey.Should().Be(corr);
        fetched.SagaName.Should().Be("BridgeTransfer");
        fetched.StepName.Should().Be("LockSource");
        fetched.Payload.Should().Be("""{"amount":"1.0"}""");
        fetched.Status.Should().Be(StepStatus.Pending);
        fetched.IsCompensation.Should().BeFalse();
        fetched.AttemptCount.Should().Be(0);
        fetched.DeadLettered.Should().BeFalse();
    }

    /// <summary>
    /// GetDueStepIdsAsync returns Pending rows whose NextRunAt is at or before
    /// now, ordered by NextRunAt ASC, bounded by batch. Future-due rows must be
    /// excluded.
    /// </summary>
    [SkippableFact]
    public async Task GetDueStepIds_ReturnsDuePendingRows_InOrder_BoundedByBatch()
    {Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var now = DateTime.UtcNow;

        // Two due rows (NextRunAt = now), one not-yet-due row (NextRunAt = now + 1h).
        var due1 = await _store.EnqueueAsync(
            "S", "Step1", UniqueCorrelation(), UniqueIdempotency(), "{}", false, CancellationToken.None);
        var due2 = await _store.EnqueueAsync(
            "S", "Step2", UniqueCorrelation(), UniqueIdempotency(), "{}", false, CancellationToken.None);
        var notDue = await _store.EnqueueAsync(
            "S", "Step3", UniqueCorrelation(), UniqueIdempotency(), "{}", false, CancellationToken.None);

        // Push notDue's NextRunAt out by an hour via a direct conditional UPDATE
        // (mirrors the production ScheduleRetry codepath without bumping attempt_count).
        await PushNextRunAtAsync(notDue.Id, now.AddHours(1));

        var ids = await _store.GetDueStepIdsAsync(
            now: now.AddSeconds(5),
            batch: 10,
            leaseTimeout: TimeSpan.FromMinutes(5),
            ct: CancellationToken.None);

        ids.Should().Contain(due1.Id);
        ids.Should().Contain(due2.Id);
        ids.Should().NotContain(notDue.Id, "not-yet-due rows must be excluded by the WHERE next_run_at <= now predicate");

        // Bounded by batch: a batch=1 call must return at most one row.
        var bounded = await _store.GetDueStepIdsAsync(
            now: now.AddSeconds(5),
            batch: 1,
            leaseTimeout: TimeSpan.FromMinutes(5),
            ct: CancellationToken.None);
        bounded.Count.Should().BeLessOrEqualTo(1, "batch parameter must bound the result");
    }

    /// <summary>
    /// GetDueStepIdsAsync reclaims InProgress rows whose claimed_at lapsed the
    /// lease — the crash-safe re-entry guarantee. An expired lease becomes due
    /// again and appears in the next scan.
    /// </summary>
    [SkippableFact]
    public async Task GetDueStepIds_ReclaimsStaleLeases()
    {Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var enq = await _store.EnqueueAsync(
            "S", "StaleStep", UniqueCorrelation(), UniqueIdempotency(),
            "{}", false, CancellationToken.None);

        // Move the row to InProgress with a claimed_at far in the past.
        await SetInProgressAtPastTimeAsync(enq.Id, DateTime.UtcNow.AddHours(-1));

        // A 5-minute lease + a claimed_at 1h ago means "lapsed" — the row must
        // be reclaimed to Pending and appear in the due-step scan.
        var due = await _store.GetDueStepIdsAsync(
            now: DateTime.UtcNow,
            batch: 10,
            leaseTimeout: TimeSpan.FromMinutes(5),
            ct: CancellationToken.None);

        due.Should().Contain(enq.Id, "stale-lease row must be reclaimed and returned to the due scan");

        var refetched = await _store.GetAsync(enq.Id, CancellationToken.None);
        refetched!.Status.Should().Be(StepStatus.Pending, "reclaimed row must be back to Pending");
        refetched.ClaimedAt.Should().BeNull("reclaim clears the lease");
    }

    /// <summary>
    /// G2 — TryClaimDueStepAsync first caller wins: state transitions to
    /// InProgress, ClaimedAt is set, and the returned record reflects the new
    /// state.
    /// </summary>
    [SkippableFact]
    public async Task TryClaimDueStep_FirstCaller_Wins()
    {Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var enq = await _store.EnqueueAsync(
            "S", "Claimable", UniqueCorrelation(), UniqueIdempotency(),
            "{}", false, CancellationToken.None);

        var now = DateTime.UtcNow.AddSeconds(5);
        var claimed = await _store.TryClaimDueStepAsync(enq.Id, now, CancellationToken.None);

        claimed.Should().NotBeNull("first caller must win the conditional claim");
        claimed!.Id.Should().Be(enq.Id);
        claimed.Status.Should().Be(StepStatus.InProgress);
        claimed.ClaimedAt.Should().NotBeNull();
    }

    /// <summary>
    /// G2 — TryClaimDueStepAsync second concurrent caller loses: the row is
    /// already InProgress, so the WHERE status==Pending predicate matches zero
    /// rows and the loser sees null. This is the single-winner property
    /// translated to SurrealDB.
    /// </summary>
    [SkippableFact]
    public async Task TryClaimDueStep_SecondConcurrentCaller_Loses()
    {Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var enq = await _store.EnqueueAsync(
            "S", "Contested", UniqueCorrelation(), UniqueIdempotency(),
            "{}", false, CancellationToken.None);

        var now = DateTime.UtcNow.AddSeconds(5);
        var first  = await _store.TryClaimDueStepAsync(enq.Id, now, CancellationToken.None);
        var second = await _store.TryClaimDueStepAsync(enq.Id, now, CancellationToken.None);

        first.Should().NotBeNull("first caller wins");
        second.Should().BeNull("second caller must lose -- exactly-one-winner contract");

        // Verify the state was not perturbed by the loser.
        var still = await _store.GetAsync(enq.Id, CancellationToken.None);
        still!.Status.Should().Be(StepStatus.InProgress,
            "loser must not regress the state of the winner's row");
    }

    /// <summary>
    /// CompleteStepAsync transitions InProgress → Completed and stores the
    /// output. A second call (now on a Completed row) is a no-op because the
    /// WHERE status==InProgress predicate matches nothing.
    /// </summary>
    [SkippableFact]
    public async Task CompleteStep_FromInProgress_Succeeds_And_IsNoOpAfterwards()
    {Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var enq = await _store.EnqueueAsync(
            "S", "Completable", UniqueCorrelation(), UniqueIdempotency(),
            "{}", false, CancellationToken.None);

        // Claim it first so the WHERE status==InProgress guard is satisfied.
        var claimed = await _store.TryClaimDueStepAsync(
            enq.Id, DateTime.UtcNow.AddSeconds(5), CancellationToken.None);
        claimed.Should().NotBeNull();

        var ok1 = await _store.CompleteStepAsync(
            enq.Id, """{"result":"ok"}""", CancellationToken.None);
        ok1.Should().BeTrue("a Completed transition from InProgress must succeed");

        var fetched = await _store.GetAsync(enq.Id, CancellationToken.None);
        fetched!.Status.Should().Be(StepStatus.Completed);
        fetched.Output.Should().Be("""{"result":"ok"}""");
        fetched.ClaimedAt.Should().BeNull("Complete clears the lease");

        // Second call must be a silent no-op (zero affected rows).
        var ok2 = await _store.CompleteStepAsync(enq.Id, "ignored", CancellationToken.None);
        ok2.Should().BeFalse("subsequent Complete on a non-InProgress row must be a no-op");
    }

    /// <summary>
    /// ScheduleRetryAsync bumps AttemptCount, pushes NextRunAt out, returns the
    /// row to Pending, and stores the error. Conditional on status==InProgress;
    /// a Pending row cannot be retried (zero affected).
    /// </summary>
    [SkippableFact]
    public async Task ScheduleRetry_FromInProgress_BumpsAttempt_AndPushesNextRunAt()
    {Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var enq = await _store.EnqueueAsync(
            "S", "Retryable", UniqueCorrelation(), UniqueIdempotency(),
            "{}", false, CancellationToken.None);

        await _store.TryClaimDueStepAsync(
            enq.Id, DateTime.UtcNow.AddSeconds(5), CancellationToken.None);

        var retryAt = DateTime.UtcNow.AddSeconds(30);
        var ok = await _store.ScheduleRetryAsync(
            enq.Id, retryAt, "transient blockchain rpc error", CancellationToken.None);
        ok.Should().BeTrue();

        var fetched = await _store.GetAsync(enq.Id, CancellationToken.None);
        fetched!.Status.Should().Be(StepStatus.Pending, "retry returns the row to Pending");
        fetched.AttemptCount.Should().Be(1, "retry bumps attempt_count");
        fetched.LastError.Should().Be("transient blockchain rpc error");
        fetched.ClaimedAt.Should().BeNull("retry clears the lease");
        fetched.NextRunAt.Should().BeOnOrAfter(retryAt.AddSeconds(-1),
            "NextRunAt must be pushed out to the supplied retryAt");
    }

    /// <summary>
    /// CompensateStepAsync transitions InProgress → Compensating AND enqueues
    /// the declared compensation row as a fresh Pending — both must be
    /// observable when the conditional transition wins.
    /// </summary>
    [SkippableFact]
    public async Task CompensateStep_FromInProgress_TransitionsAndEnqueuesCompensation()
    {Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var corr = UniqueCorrelation();
        var enq = await _store.EnqueueAsync(
            sagaName: "BridgeTransfer",
            stepName: "LockSource",
            correlationKey: corr,
            stepIdempotencyKey: UniqueIdempotency(),
            payloadJson: """{"amount":"1.0"}""",
            isCompensation: false,
            ct: CancellationToken.None);

        await _store.TryClaimDueStepAsync(
            enq.Id, DateTime.UtcNow.AddSeconds(5), CancellationToken.None);

        var compensationIdem = UniqueIdempotency();
        var compensation = await _store.CompensateStepAsync(
            id: enq.Id,
            compensationStepName: "UnlockSource",
            compensationIdempotencyKey: compensationIdem,
            compensationPayloadJson: """{"reverse":"1.0"}""",
            error: "redeem failed -- compensating",
            ct: CancellationToken.None);

        compensation.Should().NotBeNull("conditional transition winner must enqueue the compensation row");
        compensation!.IsCompensation.Should().BeTrue();
        compensation.Status.Should().Be(StepStatus.Pending);
        compensation.StepName.Should().Be("UnlockSource");
        compensation.CorrelationKey.Should().Be(corr, "compensation shares the failing step's correlation");
        compensation.StepIdempotencyKey.Should().Be(compensationIdem);

        // The failing row must now be Compensating with last_error populated.
        var failing = await _store.GetAsync(enq.Id, CancellationToken.None);
        failing!.Status.Should().Be(StepStatus.Compensating);
        failing.LastError.Should().Be("redeem failed -- compensating");
        failing.AttemptCount.Should().Be(1);
        failing.ClaimedAt.Should().BeNull("compensation clears the lease");
    }

    /// <summary>
    /// DeadLetterStepAsync transitions InProgress → DeadLettered, sets the
    /// DeadLettered=true mirror flag, bumps AttemptCount, stores the error.
    /// </summary>
    [SkippableFact]
    public async Task DeadLetterStep_FromInProgress_TransitionsAndSetsFlag()
    {Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var enq = await _store.EnqueueAsync(
            "S", "DoomedStep", UniqueCorrelation(), UniqueIdempotency(),
            "{}", false, CancellationToken.None);

        await _store.TryClaimDueStepAsync(
            enq.Id, DateTime.UtcNow.AddSeconds(5), CancellationToken.None);

        var ok = await _store.DeadLetterStepAsync(
            enq.Id, "no compensation declared -- terminal failure", CancellationToken.None);
        ok.Should().BeTrue();

        var fetched = await _store.GetAsync(enq.Id, CancellationToken.None);
        fetched!.Status.Should().Be(StepStatus.DeadLettered);
        fetched.DeadLettered.Should().BeTrue("mirror flag must be set");
        fetched.LastError.Should().Be("no compensation declared -- terminal failure");
        fetched.AttemptCount.Should().Be(1);
        fetched.ClaimedAt.Should().BeNull();
    }

    // ── Test-only helpers (direct SurrealQL through the same executor) ────────

    /// <summary>
    /// Test-helper: push <c>next_run_at</c> of the given row out without going
    /// through the production codepath (so we can stage a "not-yet-due" row).
    /// </summary>
    private async Task PushNextRunAtAsync(Guid id, DateTime futureUtc)
    {
        var executor = new DefaultSurrealExecutor(_connection);
        var q = SurrealQuery
            .Of("UPDATE type::record($_t, $_id) SET next_run_at = $_next, updated_at = $_now")
            .WithParam("_t", "saga_steps")
            .WithParam("_id", id.ToString("N").ToLowerInvariant())
            .WithParam("_next", DateTime.SpecifyKind(futureUtc, DateTimeKind.Utc))
            .WithParam("_now", DateTime.UtcNow);
        var resp = await executor.ExecuteAsync(q, CancellationToken.None);
        resp.EnsureAllOk();
    }

    /// <summary>
    /// Test-helper: stage a row as InProgress with a claimed_at in the past so
    /// the stale-lease reclaim codepath can be exercised.
    /// </summary>
    private async Task SetInProgressAtPastTimeAsync(Guid id, DateTime claimedAtUtc)
    {
        var executor = new DefaultSurrealExecutor(_connection);
        var q = SurrealQuery
            .Of("UPDATE type::record($_t, $_id) SET status = 'InProgress', claimed_at = $_claimed, updated_at = $_now")
            .WithParam("_t", "saga_steps")
            .WithParam("_id", id.ToString("N").ToLowerInvariant())
            .WithParam("_claimed", DateTime.SpecifyKind(claimedAtUtc, DateTimeKind.Utc))
            .WithParam("_now", DateTime.UtcNow);
        var resp = await executor.ExecuteAsync(q, CancellationToken.None);
        resp.EnsureAllOk();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string UniqueCorrelation() => $"corr-{Guid.NewGuid():N}";
    private static string UniqueIdempotency() => $"idem-{Guid.NewGuid():N}";

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
    /// Applies the minimal DDL for <c>saga_steps</c> matching
    /// <c>Persistence/SurrealDb/Schemas/080_saga_steps.surql</c>. Self-contained
    /// so tests don't depend on the schema runner having executed beforehand.
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

        // DDL mirrors 080_saga_steps.surql. Uses IF NOT EXISTS so it is safe to
        // re-apply, and FLEXIBLE TYPE on option<...> fields so SurrealDB does
        // not reject explicit NULLs on CREATE (the production runner does the
        // same for 070_idempotency_key_store).
        const string ddl = """
            DEFINE NAMESPACE IF NOT EXISTS $ns;
            USE NS $ns DB test;
            DEFINE DATABASE IF NOT EXISTS test;
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

        var content = new StringContent(ddl, System.Text.Encoding.UTF8, "text/plain");
        var response = await ddlClient.PostAsync("/sql", content);
        // Best-effort -- tests may still work if table already exists.
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
