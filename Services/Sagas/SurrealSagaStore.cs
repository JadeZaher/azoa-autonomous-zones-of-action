using System.Text.Json;
using System.Text.Json.Serialization;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Models.Sagas;

namespace AZOA.WebAPI.Sagas;

/// <summary>
/// SurrealDB-backed implementation of <see cref="ISagaStore"/>. All durable
/// specifics live here, behind the seam. Every mutating operation is an atomic
/// conditional update — the same single-winner discipline the
/// api-safety-hardening conditional-UPDATE primitives proved on the prior EF
/// path and that <c>ReconciliationService</c> still relies on, translated to
/// SurrealQL via the <see cref="SurrealQuery"/> safe-layer builder.
///
/// <para><b>G2 — single-winner claim.</b> <see cref="TryClaimDueStepAsync"/> is
/// the exactly-once executor primitive: an atomic conditional UPDATE
/// (<c>WHERE id == &lt;id&gt; AND status == 'Pending' AND next_run_at &lt;= now</c>
/// SET <c>status='InProgress', claimed_at=now</c>) that asserts exactly one row
/// changed. Identical contract to the EF implementation, identical race
/// semantics — the conditional predicate (NOT optimistic concurrency) is the
/// arbiter.</para>
///
/// <para><b>G3 — no string interpolation.</b> Every value reaches SurrealDB via
/// <see cref="SurrealQuery.WithParam"/> bindings; the table identifier is
/// hard-coded ("saga_steps") and only flows through <c>type::record($_t, $_id)</c>
/// so it cannot be smuggled by callers.</para>
///
/// <para><b>POCO seam.</b> The repo does not yet emit a generated POCO for the
/// new <c>saga_steps</c> table (it is added in this commit), so a private
/// <see cref="SagaStepPoco"/> handles the SurrealDB ↔ domain mapping. When the
/// source generator catches up the POCO can be deleted with no contract change.</para>
/// </summary>
public sealed class SurrealSagaStore : ISagaStore
{
    private const string TableName = "saga_steps";

    // Status string literals are passed as bound parameters so SurrealDB's
    // ASSERT INSIDE [...] constraint compares against the same tokens the
    // schema declares — no token smuggling, no typo drift.
    private const string StatusPending      = "Pending";
    private const string StatusInProgress   = "InProgress";
    private const string StatusCompleted    = "Completed";
    private const string StatusCompensating = "Compensating";
    private const string StatusDeadLettered = "DeadLettered";
    private const string StatusParked       = "Parked";

    private const int LastErrorMaxLength = 2048;
    private const int OutputMaxLength    = 4096;

    private readonly ISurrealExecutor _executor;

    public SurrealSagaStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    // ── EnqueueAsync ──────────────────────────────────────────────────────────

    public async Task<SagaStepRecord> EnqueueAsync(
        string sagaName,
        string stepName,
        string correlationKey,
        string stepIdempotencyKey,
        string payloadJson,
        bool isCompensation,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var record = new SagaStepRecord
        {
            Id = Guid.NewGuid(),
            SagaName = sagaName,
            StepName = stepName,
            CorrelationKey = correlationKey,
            StepIdempotencyKey = stepIdempotencyKey,
            Payload = payloadJson,
            Status = StepStatus.Pending,
            IsCompensation = isCompensation,
            AttemptCount = 0,
            NextRunAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await PersistNewAsync(record, ct);
        return record;
    }

    public Task<SagaStepRecord> EnqueueNextStepAsync(
        string sagaName,
        string nextStepName,
        string correlationKey,
        string stepIdempotencyKey,
        string payloadJson,
        CancellationToken ct)
        => EnqueueAsync(sagaName, nextStepName, correlationKey,
            stepIdempotencyKey, payloadJson, isCompensation: false, ct);

    private async Task PersistNewAsync(SagaStepRecord record, CancellationToken ct)
    {
        var poco = FromDomain(record);

        // CREATE type::record('saga_steps', <id>) CONTENT <body> RETURN AFTER.
        // type::record() builds the SurrealDB record id from a parameterized
        // table+id pair so the table identifier cannot be smuggled.
        var q = SurrealQuery
            .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
            .WithParam("_t", TableName)
            .WithParam("_id", poco.Id)
            .WithParam("_body", poco);

        var response = await _executor.ExecuteAsync(q, ct);
        response.EnsureAllOk();
    }

    // ── ParkStepAsync — suspend on signal/timer (durable-workflow-engine) ─────

    public async Task<bool> ParkStepAsync(
        Guid id, string gateId, DateTime? resumeAt, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var surrealId = SurrealId.ToSurrealId(id);

        // The park KIND is the single discriminator (no magic sentinel, no
        // empty-string overload — review finding):
        //   • TIMER park  (resumeAt set)  ⇒ gate_id = NONE, next_run_at = resumeAt.
        //     GetDueStepIdsAsync's fire-timers clause (gate_id NONE AND
        //     next_run_at <= now) auto-resumes it; no signal needed.
        //   • SIGNAL park (resumeAt null) ⇒ gate_id = <gate>, next_run_at LEFT
        //     UNCHANGED. The due scan never claims it (status is Parked, not
        //     Pending) and fire-timers never fires it (gate_id is not NONE), so
        //     ONLY TrySignalAsync un-parks it — no far-future datetime sentinel.
        // Conditional on InProgress — same single-winner discipline as Complete.
        // Two literal query variants keep gate_id strictly NONE-or-real (G3: no
        // value interpolation; the variant choice is structural, not data).
        SurrealQuery q;
        if (resumeAt.HasValue)
        {
            var nextRunUtc = DateTime.SpecifyKind(resumeAt.Value, DateTimeKind.Utc);
            q = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) SET status = $_parked, gate_id = NONE, next_run_at = $_next, claimed_at = NONE, updated_at = $_now WHERE status = $_in_progress RETURN AFTER")
                .WithParam("_t", TableName)
                .WithParam("_id", surrealId)
                .WithParam("_parked", StatusParked)
                .WithParam("_next", nextRunUtc)
                .WithParam("_in_progress", StatusInProgress)
                .WithParam("_now", nowUtc);
        }
        else
        {
            q = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) SET status = $_parked, gate_id = $_gate, claimed_at = NONE, updated_at = $_now WHERE status = $_in_progress RETURN AFTER")
                .WithParam("_t", TableName)
                .WithParam("_id", surrealId)
                .WithParam("_parked", StatusParked)
                .WithParam("_gate", gateId)
                .WithParam("_in_progress", StatusInProgress)
                .WithParam("_now", nowUtc);
        }

        var response = await _executor.ExecuteAsync(q, ct);
        if (response.Count == 0 || !response[0].IsOk) return false;
        return response[0].AffectedCount() == 1;
    }

    // ── TrySignalAsync — un-park a gate step (G2 single-winner) ───────────────

    public async Task<SagaStepRecord?> TrySignalAsync(
        string correlationKey, string gateId, string? newPayloadJson, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;

        // G2 single-winner un-park. The predicate (status==Parked AND matching
        // correlation+gate) is the arbiter: a duplicate/racing signal sees the
        // row already Pending and mutates zero rows. Un-parking sets next_run_at
        // to now so the very next due scan resumes the step, and clears gate_id.
        // The signal body (when supplied) is written in the SAME atomic
        // statement so the processor can never claim a now-due row whose payload
        // still lacks the signal body (no un-park/stamp race — review finding H).
        // Two fully-literal query variants (G3: no interpolation) — the
        // payload-stamping one adds the payload SET + a bound $_payload param.
        SurrealQuery q;
        if (newPayloadJson is not null)
        {
            q = SurrealQuery
                .Of("UPDATE saga_steps SET status = $_pending, next_run_at = $_now, gate_id = NONE, payload = $_payload, updated_at = $_now WHERE status = $_parked AND correlation_key = $_corr AND gate_id = $_gate RETURN AFTER")
                .WithParam("_pending", StatusPending)
                .WithParam("_parked", StatusParked)
                .WithParam("_corr", correlationKey)
                .WithParam("_gate", gateId)
                .WithParam("_payload", newPayloadJson)
                .WithParam("_now", nowUtc);
        }
        else
        {
            q = SurrealQuery
                .Of("UPDATE saga_steps SET status = $_pending, next_run_at = $_now, gate_id = NONE, updated_at = $_now WHERE status = $_parked AND correlation_key = $_corr AND gate_id = $_gate RETURN AFTER")
                .WithParam("_pending", StatusPending)
                .WithParam("_parked", StatusParked)
                .WithParam("_corr", correlationKey)
                .WithParam("_gate", gateId)
                .WithParam("_now", nowUtc);
        }

        var response = await _executor.ExecuteAsync(q, ct);
        if (response.Count == 0 || !response[0].IsOk)
            return null;

        // At most one row should match a given (correlation, gate) park; if more
        // than one un-parks, the first is returned and the rest still resume via
        // the due scan. Zero affected ⇒ nothing was parked on this gate.
        if (response[0].AffectedCount() < 1)
            return null;

        var pocos = response[0].GetValues<SagaStepPoco>();
        return pocos.Count >= 1 ? ToDomain(pocos[0]) : null;
    }

    // ── GetDueStepIdsAsync ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Guid>> GetDueStepIdsAsync(
        DateTime now, int batch, TimeSpan leaseTimeout, CancellationToken ct)
    {
        var safeBatch = Math.Clamp(batch, 1, 1000);
        var nowUtc = DateTime.SpecifyKind(now, DateTimeKind.Utc);
        var leaseCutoff = nowUtc - leaseTimeout;

        // Three-statement transaction:
        //   [0] Reclaim stale leases: any InProgress row whose claimed_at is
        //       older than the lease boundary is a crashed processor — return
        //       it to Pending+due so it is included in the scan.
        //   [1] Fire due timers: a TIMER-armed Parked row (gate_id IS NONE — a
        //       pure wait node) whose next_run_at has passed returns to Pending
        //       so it auto-resumes. Signal-only parks carry a non-NONE gate_id
        //       (and never touch next_run_at), so they are never timer-due and
        //       only TrySignalAsync un-parks them. gate_id NONE-vs-real is the
        //       sole timer/signal discriminator — no empty-string, no sentinel.
        //   [2] Select due step ids ordered by next_run_at ASC, bounded by batch.
        //
        // Statements [0]/[1] are conditional UPDATEs — only the truly-due rows
        // mutate. Crash-safe re-entry. The handler keying its effect on the
        // stable step_idempotency_key means a resumed step is an idempotent
        // replay (via IIdempotencyStore), never a double-run.
        var reclaim = SurrealQuery
            .Of("UPDATE saga_steps SET status = $_pending, next_run_at = $_now, claimed_at = NONE, updated_at = $_now WHERE status = $_in_progress AND claimed_at != NONE AND claimed_at < $_lease_cutoff")
            .WithParam("_pending", StatusPending)
            .WithParam("_in_progress", StatusInProgress)
            .WithParam("_now", nowUtc)
            .WithParam("_lease_cutoff", leaseCutoff);

        var fireTimers = SurrealQuery
            .Of("UPDATE saga_steps SET status = $_pending, updated_at = $_now WHERE status = $_parked AND gate_id = NONE AND next_run_at <= $_now")
            .WithParam("_pending", StatusPending)
            .WithParam("_parked", StatusParked)
            .WithParam("_now", nowUtc);

        var scan = SurrealQuery
            // SurrealDB 3.x requires the ORDER BY idiom to appear in the
            // SELECT projection; include next_run_at (the id projection ignores it).
            .Of("SELECT id, next_run_at FROM saga_steps WHERE status = $_pending AND next_run_at <= $_now ORDER BY next_run_at ASC LIMIT $_batch")
            .WithParam("_pending", StatusPending)
            .WithParam("_now", nowUtc)
            .WithParam("_batch", safeBatch);

        var combined = SurrealQuery.Combine(reclaim, fireTimers, scan);
        var response = await _executor.ExecuteAsync(combined, ct);
        response.EnsureAllOk();

        // Statement [2]: SELECT id projects to { "id": <thing> }; thing
        // serialization renders as a string of the form "saga_steps:<hex>" or
        // an object { "tb": "saga_steps", "id": "<hex>" }. We deserialize via
        // a minimal projection that accepts either shape.
        var idRows = response.GetValues<SagaStepIdProjection>(2);

        var result = new List<Guid>(idRows.Count);
        foreach (var row in idRows)
        {
            var raw = row.Id;
            if (string.IsNullOrEmpty(raw)) continue;
            var hex = ExtractRecordIdSuffix(raw);
            if (Guid.TryParseExact(hex, "N", out var guid))
                result.Add(guid);
        }
        return result;
    }

    // ── TryClaimDueStepAsync — G2 single-winner primitive ─────────────────────

    public async Task<SagaStepRecord?> TryClaimDueStepAsync(
        Guid id, DateTime now, CancellationToken ct)
    {
        var nowUtc = DateTime.SpecifyKind(now, DateTimeKind.Utc);
        var surrealId = SurrealId.ToSurrealId(id);

        // THE single-winner primitive. Atomic conditional UPDATE on a single
        // record; the predicate (status==Pending + still due) is the arbiter.
        // Under N racing processors at most one update mutates a row — the
        // others see AffectedCount==0 and return null. Mirrors
        // the prior EF conditional UPDATE (status=Pending WHERE clause) discipline.
        //
        // UpdateOnly cannot express three SET fields + three WHERE conditions
        // in its current minimal shape, so the query drops down to a typed
        // SurrealQuery.Of with parameterized bindings (G3 still enforced).
        var q = SurrealQuery
            .Of("UPDATE type::record($_t, $_id) SET status = $_in_progress, claimed_at = $_now, updated_at = $_now WHERE status = $_pending AND next_run_at <= $_now RETURN AFTER")
            .WithParam("_t", TableName)
            .WithParam("_id", surrealId)
            .WithParam("_pending", StatusPending)
            .WithParam("_in_progress", StatusInProgress)
            .WithParam("_now", nowUtc);

        var response = await _executor.ExecuteAsync(q, ct);
        if (response.Count == 0 || !response[0].IsOk)
            return null;

        var affected = response[0].AffectedCount();
        if (affected != 1)
            return null; // lost the race / no longer due — single winner only

        var pocos = response[0].GetValues<SagaStepPoco>();
        return pocos.Count == 1 ? ToDomain(pocos[0]) : null;
    }

    // ── CompleteStepAsync ─────────────────────────────────────────────────────

    public async Task<bool> CompleteStepAsync(Guid id, string? output, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var surrealId = SurrealId.ToSurrealId(id);
        var truncatedOutput = Truncate(output, OutputMaxLength);

        var q = SurrealQuery
            .Of("UPDATE type::record($_t, $_id) SET status = $_completed, output = $_output, claimed_at = NONE, updated_at = $_now WHERE status = $_in_progress RETURN AFTER")
            .WithParam("_t", TableName)
            .WithParam("_id", surrealId)
            .WithParam("_completed", StatusCompleted)
            .WithParam("_in_progress", StatusInProgress)
            .WithParam("_output", (object?)truncatedOutput!)
            .WithParam("_now", nowUtc);

        var response = await _executor.ExecuteAsync(q, ct);
        if (response.Count == 0 || !response[0].IsOk) return false;
        return response[0].AffectedCount() == 1;
    }

    // ── ScheduleRetryAsync ────────────────────────────────────────────────────

    public async Task<bool> ScheduleRetryAsync(
        Guid id, DateTime nextRunAt, string error, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var nextRunUtc = DateTime.SpecifyKind(nextRunAt, DateTimeKind.Utc);
        var surrealId = SurrealId.ToSurrealId(id);

        var q = SurrealQuery
            .Of("UPDATE type::record($_t, $_id) SET status = $_pending, attempt_count = attempt_count + 1, next_run_at = $_next, claimed_at = NONE, last_error = $_error, updated_at = $_now WHERE status = $_in_progress RETURN AFTER")
            .WithParam("_t", TableName)
            .WithParam("_id", surrealId)
            .WithParam("_pending", StatusPending)
            .WithParam("_in_progress", StatusInProgress)
            .WithParam("_next", nextRunUtc)
            .WithParam("_error", (object?)Truncate(error, LastErrorMaxLength)!)
            .WithParam("_now", nowUtc);

        var response = await _executor.ExecuteAsync(q, ct);
        if (response.Count == 0 || !response[0].IsOk) return false;
        return response[0].AffectedCount() == 1;
    }

    // ── CompensateStepAsync ───────────────────────────────────────────────────

    public async Task<SagaStepRecord?> CompensateStepAsync(
        Guid id,
        string compensationStepName,
        string compensationIdempotencyKey,
        string compensationPayloadJson,
        string error,
        CancellationToken ct)
    {
        var failing = await GetAsync(id, ct);
        if (failing is null) return null;

        var nowUtc = DateTime.UtcNow;
        var surrealId = SurrealId.ToSurrealId(id);

        // Build the compensation row up-front so we can ship both statements
        // in a single multi-statement Combine. Statement [0] is the conditional
        // transition (only the winner mutates); statement [1] CREATEs the new
        // compensation Pending row. If [0]'s WHERE clause misses (concurrent
        // reclaim won) we surface that as "no winner" by inspecting affected==0
        // and skipping the result of [1] (which will still have CREATEd because
        // Combine is not a SurrealDB transaction). To make the two-statement
        // sequence truly atomic we wrap them in BEGIN/COMMIT via the explicit
        // transaction primitive instead.
        var compensation = new SagaStepRecord
        {
            Id = Guid.NewGuid(),
            SagaName = failing.SagaName,
            StepName = compensationStepName,
            CorrelationKey = failing.CorrelationKey,
            StepIdempotencyKey = compensationIdempotencyKey,
            Payload = compensationPayloadJson,
            Status = StepStatus.Pending,
            IsCompensation = true,
            AttemptCount = 0,
            NextRunAt = nowUtc,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
        };
        var compensationPoco = FromDomain(compensation);

        // First: conditional transition of the failing step. Only proceed to
        // enqueue the compensation row when this winner-of-the-race succeeded.
        var transition = SurrealQuery
            .Of("UPDATE type::record($_t, $_id) SET status = $_compensating, attempt_count = attempt_count + 1, claimed_at = NONE, last_error = $_error, updated_at = $_now WHERE status = $_in_progress RETURN AFTER")
            .WithParam("_t", TableName)
            .WithParam("_id", surrealId)
            .WithParam("_compensating", StatusCompensating)
            .WithParam("_in_progress", StatusInProgress)
            .WithParam("_error", (object?)Truncate(error, LastErrorMaxLength)!)
            .WithParam("_now", nowUtc);

        var transitionResp = await _executor.ExecuteAsync(transition, ct);
        if (transitionResp.Count == 0 || !transitionResp[0].IsOk)
            return null;
        if (transitionResp[0].AffectedCount() != 1)
            return null; // concurrent transition already handled it

        // Second: enqueue the declared compensation row as a fresh Pending.
        // Sequenced AFTER the conditional transition succeeded so the same
        // single-winner property keeps it from being double-enqueued.
        var create = SurrealQuery
            .Of("CREATE type::record($_t2, $_cid) CONTENT $_cbody RETURN AFTER")
            .WithParam("_t2", TableName)
            .WithParam("_cid", compensationPoco.Id)
            .WithParam("_cbody", compensationPoco);

        var createResp = await _executor.ExecuteAsync(create, ct);
        createResp.EnsureAllOk();

        return compensation;
    }

    // ── DeadLetterStepAsync ───────────────────────────────────────────────────

    public async Task<bool> DeadLetterStepAsync(Guid id, string error, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var surrealId = SurrealId.ToSurrealId(id);

        var q = SurrealQuery
            .Of("UPDATE type::record($_t, $_id) SET status = $_dead, dead_lettered = true, attempt_count = attempt_count + 1, claimed_at = NONE, last_error = $_error, updated_at = $_now WHERE status = $_in_progress RETURN AFTER")
            .WithParam("_t", TableName)
            .WithParam("_id", surrealId)
            .WithParam("_dead", StatusDeadLettered)
            .WithParam("_in_progress", StatusInProgress)
            .WithParam("_error", (object?)Truncate(error, LastErrorMaxLength)!)
            .WithParam("_now", nowUtc);

        var response = await _executor.ExecuteAsync(q, ct);
        if (response.Count == 0 || !response[0].IsOk) return false;
        return response[0].AffectedCount() == 1;
    }

    // ── GetParkedStepAsync — read parked row for signal-payload derivation ────

    public async Task<SagaStepRecord?> GetParkedStepAsync(
        string correlationKey, string gateId, CancellationToken ct)
    {
        var q = SurrealQuery
            .Of("SELECT * FROM saga_steps WHERE status = $_parked AND correlation_key = $_corr AND gate_id = $_gate LIMIT 1")
            .WithParam("_parked", StatusParked)
            .WithParam("_corr", correlationKey)
            .WithParam("_gate", gateId);

        var rows = await _executor.QueryAsync<SagaStepPoco>(q, ct);
        return rows.Count == 0 ? null : ToDomain(rows[0]);
    }

    // ── StepExistsAsync — idempotent-enqueue guard ────────────────────────────

    public async Task<bool> StepExistsAsync(
        string correlationKey, string stepName, CancellationToken ct)
    {
        var q = SurrealQuery
            .Of("SELECT id FROM saga_steps WHERE correlation_key = $_corr AND step_name = $_name LIMIT 1")
            .WithParam("_corr", correlationKey)
            .WithParam("_name", stepName);

        var rows = await _executor.QueryAsync<SagaStepIdProjection>(q, ct);
        return rows.Count > 0;
    }

    // ── GetAsync ──────────────────────────────────────────────────────────────

    public async Task<SagaStepRecord?> GetAsync(Guid id, CancellationToken ct)
    {
        var surrealId = SurrealId.ToSurrealId(id);
        var q = SurrealQuery
            .Of("SELECT * FROM type::record($_t, $_id)")
            .WithParam("_t", TableName)
            .WithParam("_id", surrealId);

        var rows = await _executor.QueryAsync<SagaStepPoco>(q, ct);
        return rows.Count == 0 ? null : ToDomain(rows[0]);
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────



    /// <summary>
    /// Extracts the trailing record-id suffix from a SurrealDB record id
    /// rendering. Accepts both <c>"saga_steps:abc..."</c> (string form) and the
    /// bare suffix (when the server already projected just the id). Anything
    /// else returns the input unchanged so callers can fall through to a parse
    /// failure rather than silently dropping the row.
    /// </summary>
    private static string ExtractRecordIdSuffix(string raw)
    {
        var colon = raw.IndexOf(':');
        return colon >= 0 && colon < raw.Length - 1 ? raw[(colon + 1)..] : raw;
    }

    private static SagaStepPoco FromDomain(SagaStepRecord r) => new()
    {
        Id                 = SurrealId.ToSurrealId(r.Id),
        CorrelationKey     = r.CorrelationKey,
        SagaName           = r.SagaName,
        StepName           = r.StepName,
        StepIdempotencyKey = r.StepIdempotencyKey,
        Payload            = r.Payload ?? string.Empty,
        Status             = StatusToString(r.Status),
        IsCompensation     = r.IsCompensation,
        AttemptCount       = r.AttemptCount,
        NextRunAt          = new DateTimeOffset(DateTime.SpecifyKind(r.NextRunAt, DateTimeKind.Utc)),
        ClaimedAt          = r.ClaimedAt.HasValue
                             ? new DateTimeOffset(DateTime.SpecifyKind(r.ClaimedAt.Value, DateTimeKind.Utc))
                             : null,
        LastError          = r.LastError,
        Output             = r.Output,
        DeadLettered       = r.DeadLettered,
        CreatedAt          = new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)),
        UpdatedAt          = new DateTimeOffset(DateTime.SpecifyKind(r.UpdatedAt, DateTimeKind.Utc)),
        GateId             = r.GateId,
    };

    private static SagaStepRecord ToDomain(SagaStepPoco p) => new()
    {
        Id                 = SurrealId.FromSurrealId(StripIdPrefix(p.Id)),
        CorrelationKey     = p.CorrelationKey ?? string.Empty,
        SagaName           = p.SagaName ?? string.Empty,
        StepName           = p.StepName ?? string.Empty,
        StepIdempotencyKey = p.StepIdempotencyKey ?? string.Empty,
        Payload            = p.Payload ?? string.Empty,
        Status             = StatusFromString(p.Status),
        IsCompensation     = p.IsCompensation,
        AttemptCount       = p.AttemptCount,
        NextRunAt          = p.NextRunAt.UtcDateTime,
        ClaimedAt          = p.ClaimedAt?.UtcDateTime,
        LastError          = p.LastError,
        Output             = p.Output,
        DeadLettered       = p.DeadLettered,
        CreatedAt          = p.CreatedAt.UtcDateTime,
        UpdatedAt          = p.UpdatedAt.UtcDateTime,
        GateId             = p.GateId,
    };

    /// <summary>
    /// Strips a leading "saga_steps:" prefix if SurrealDB returned the id in
    /// thing-form rather than as the bare suffix. Defensive — both shapes
    /// appear in practice depending on the serializer codepath.
    /// </summary>
    private static string StripIdPrefix(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var colon = raw.IndexOf(':');
        return colon >= 0 && colon < raw.Length - 1 ? raw[(colon + 1)..] : raw;
    }

    private static string StatusToString(StepStatus s) => s switch
    {
        StepStatus.Pending      => StatusPending,
        StepStatus.InProgress   => StatusInProgress,
        StepStatus.Completed    => StatusCompleted,
        StepStatus.Compensating => StatusCompensating,
        StepStatus.DeadLettered => StatusDeadLettered,
        StepStatus.Parked       => StatusParked,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, "Unknown StepStatus value."),
    };

    private static StepStatus StatusFromString(string? s) => s switch
    {
        StatusPending      => StepStatus.Pending,
        StatusInProgress   => StepStatus.InProgress,
        StatusCompleted    => StepStatus.Completed,
        StatusCompensating => StepStatus.Compensating,
        StatusDeadLettered => StepStatus.DeadLettered,
        StatusParked       => StepStatus.Parked,
        _ => throw new InvalidOperationException(
            $"Unrecognised saga step status '{s}' read from SurrealDB. " +
            "Schema ASSERT INSIDE [...] should have prevented this; refresh the schema."),
    };

    private static string? Truncate(string? s, int max) =>
        s is null || s.Length <= max ? s : s[..max];

    // ── POCO (private) ────────────────────────────────────────────────────────

    /// <summary>
    /// SurrealDB row shape for the <c>saga_steps</c> table. Private to this
    /// store because no generated POCO exists yet — when the source generator
    /// catches up to table 080 this can be deleted and the generated type
    /// substituted with no contract change.
    /// </summary>
    private sealed class SagaStepPoco
    {
        [JsonPropertyName("id")]                    public string Id { get; set; } = string.Empty;
        [JsonPropertyName("correlation_key")]       public string? CorrelationKey { get; set; }
        [JsonPropertyName("saga_name")]             public string? SagaName { get; set; }
        [JsonPropertyName("step_name")]             public string? StepName { get; set; }
        [JsonPropertyName("step_idempotency_key")]  public string? StepIdempotencyKey { get; set; }
        [JsonPropertyName("payload")]               public string? Payload { get; set; }
        [JsonPropertyName("status")]                public string? Status { get; set; }
        [JsonPropertyName("is_compensation")]       public bool IsCompensation { get; set; }
        [JsonPropertyName("attempt_count")]         public int AttemptCount { get; set; }
        [JsonPropertyName("next_run_at")]           public DateTimeOffset NextRunAt { get; set; }
        [JsonPropertyName("claimed_at")]            public DateTimeOffset? ClaimedAt { get; set; }
        [JsonPropertyName("last_error")]            public string? LastError { get; set; }
        [JsonPropertyName("output")]                public string? Output { get; set; }
        [JsonPropertyName("dead_lettered")]         public bool DeadLettered { get; set; }
        [JsonPropertyName("created_at")]            public DateTimeOffset CreatedAt { get; set; }
        [JsonPropertyName("updated_at")]            public DateTimeOffset UpdatedAt { get; set; }
        [JsonPropertyName("gate_id")]               public string? GateId { get; set; }
    }

    /// <summary>
    /// Lightweight projection for the <c>SELECT id FROM saga_steps</c>
    /// statement in <see cref="GetDueStepIdsAsync"/>. The Id payload may arrive
    /// as either a bare suffix string or a <c>{ tb, id }</c> object; we
    /// canonicalise to <c>string Id</c> with a custom converter so the
    /// downstream parse-as-Guid path is uniform.
    /// </summary>
    [JsonConverter(typeof(SagaStepIdProjectionConverter))]
    private sealed class SagaStepIdProjection
    {
        public string Id { get; set; } = string.Empty;
    }

    private sealed class SagaStepIdProjectionConverter : JsonConverter<SagaStepIdProjection>
    {
        public override SagaStepIdProjection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected start of object for SagaStepIdProjection.");

            string? raw = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                var prop = reader.GetString();
                reader.Read();
                if (!string.Equals(prop, "id", StringComparison.Ordinal))
                {
                    reader.Skip();
                    continue;
                }

                if (reader.TokenType == JsonTokenType.String)
                {
                    raw = reader.GetString();
                }
                else if (reader.TokenType == JsonTokenType.StartObject)
                {
                    // { "tb": "saga_steps", "id": "<hex>" } — extract the id.
                    string? tb = null;
                    string? innerId = null;
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.EndObject) break;
                        if (reader.TokenType != JsonTokenType.PropertyName) continue;

                        var innerProp = reader.GetString();
                        reader.Read();
                        if (innerProp == "tb" && reader.TokenType == JsonTokenType.String)
                            tb = reader.GetString();
                        else if (innerProp == "id" && reader.TokenType == JsonTokenType.String)
                            innerId = reader.GetString();
                        else
                            reader.Skip();
                    }
                    raw = tb is not null && innerId is not null ? $"{tb}:{innerId}" : innerId;
                }
                else
                {
                    reader.Skip();
                }
            }

            return new SagaStepIdProjection { Id = raw ?? string.Empty };
        }

        public override void Write(Utf8JsonWriter writer, SagaStepIdProjection value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("id", value.Id);
            writer.WriteEndObject();
        }
    }
}
