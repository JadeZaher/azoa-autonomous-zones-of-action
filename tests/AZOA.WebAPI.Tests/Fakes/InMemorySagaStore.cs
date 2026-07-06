using System.Collections.Concurrent;
using AZOA.WebAPI.Models.Sagas;
using AZOA.WebAPI.Sagas;

namespace AZOA.WebAPI.Tests.Fakes;

/// <summary>
/// In-memory <see cref="ISagaStore"/> test double that mirrors the EXACT
/// semantics of <see cref="SurrealSagaStore"/> over a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. Every mutating method is the
/// same conditional single-winner transition the SurrealDB store performs (its
/// <c>WHERE status = …</c> predicate becomes an in-memory CAS-style read +
/// guarded swap), so a saga/handler/processor driven over this store behaves
/// identically to one driven over SurrealDB — the durable-workflow-engine
/// acceptance proof needs no real database.
///
/// <para><b>Clone-on-read/write.</b> Like <see cref="AZOA.WebAPI.Providers.Stores.InMemoryQuestNodeExecutionStore"/>,
/// every value handed out (and stored) is a defensive deep copy of the
/// <see cref="SagaStepRecord"/> so a caller can never mutate the backing store
/// through a returned reference. <see cref="SagaStepRecord"/> has no Clone of its
/// own, so <see cref="Copy"/> performs the field-by-field copy.</para>
/// </summary>
public sealed class InMemorySagaStore : ISagaStore
{
    private readonly ConcurrentDictionary<Guid, SagaStepRecord> _steps = new();

    // ── EnqueueAsync / EnqueueNextStepAsync ───────────────────────────────────

    public Task<SagaStepRecord> EnqueueAsync(
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

        _steps[record.Id] = Copy(record);
        return Task.FromResult(Copy(record));
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

    // ── GetDueStepIdsAsync ────────────────────────────────────────────────────

    public Task<IReadOnlyList<Guid>> GetDueStepIdsAsync(
        DateTime now, int batch, TimeSpan leaseTimeout, CancellationToken ct)
    {
        var safeBatch = Math.Clamp(batch, 1, 1000);
        var nowUtc = DateTime.SpecifyKind(now, DateTimeKind.Utc);
        var leaseCutoff = nowUtc - leaseTimeout;

        // [0] Reclaim stale leases: InProgress whose ClaimedAt is older than the
        //     lease boundary is a crashed processor — return it to Pending+due.
        foreach (var rec in _steps.Values.ToList())
        {
            if (rec.Status == StepStatus.InProgress
                && rec.ClaimedAt.HasValue
                && DateTime.SpecifyKind(rec.ClaimedAt.Value, DateTimeKind.Utc) < leaseCutoff)
            {
                MutateIf(rec.Id,
                    r => r.Status == StepStatus.InProgress
                         && r.ClaimedAt.HasValue
                         && DateTime.SpecifyKind(r.ClaimedAt.Value, DateTimeKind.Utc) < leaseCutoff,
                    r =>
                    {
                        r.Status = StepStatus.Pending;
                        r.NextRunAt = nowUtc;
                        r.ClaimedAt = null;
                        r.UpdatedAt = nowUtc;
                    });
            }
        }

        // [1] FIRE DUE TIMERS: a TIMER-armed Parked row (empty/null gate id — a
        //     pure wait node) whose NextRunAt has passed returns to Pending so it
        //     auto-resumes. Signal-only parks carry a non-empty gate id + the
        //     far-future sentinel, so they are never timer-due. ESSENTIAL — without
        //     this, timer nodes never resume.
        foreach (var rec in _steps.Values.ToList())
        {
            if (rec.Status == StepStatus.Parked
                && string.IsNullOrEmpty(rec.GateId)
                && DateTime.SpecifyKind(rec.NextRunAt, DateTimeKind.Utc) <= nowUtc)
            {
                MutateIf(rec.Id,
                    r => r.Status == StepStatus.Parked
                         && string.IsNullOrEmpty(r.GateId)
                         && DateTime.SpecifyKind(r.NextRunAt, DateTimeKind.Utc) <= nowUtc,
                    r =>
                    {
                        r.Status = StepStatus.Pending;
                        r.GateId = null;
                        r.UpdatedAt = nowUtc;
                    });
            }
        }

        // [2] Select due step ids: Pending AND NextRunAt <= now, oldest first,
        //     bounded by batch.
        var dueIds = _steps.Values
            .Where(r => r.Status == StepStatus.Pending
                        && DateTime.SpecifyKind(r.NextRunAt, DateTimeKind.Utc) <= nowUtc)
            .OrderBy(r => r.NextRunAt)
            .Take(safeBatch)
            .Select(r => r.Id)
            .ToList();

        return Task.FromResult<IReadOnlyList<Guid>>(dueIds);
    }

    // ── TryClaimDueStepAsync — single-winner primitive ────────────────────────

    public Task<SagaStepRecord?> TryClaimDueStepAsync(
        Guid id, DateTime now, CancellationToken ct)
    {
        var nowUtc = DateTime.SpecifyKind(now, DateTimeKind.Utc);

        var claimed = MutateIf(id,
            r => r.Status == StepStatus.Pending
                 && DateTime.SpecifyKind(r.NextRunAt, DateTimeKind.Utc) <= nowUtc,
            r =>
            {
                r.Status = StepStatus.InProgress;
                r.ClaimedAt = nowUtc;
                r.UpdatedAt = nowUtc;
            });

        // Single winner: only the caller whose conditional update applied gets the
        // record; everyone else (lost race / not due) gets null.
        return Task.FromResult(claimed);
    }

    // ── CompleteStepAsync ─────────────────────────────────────────────────────

    public Task<bool> CompleteStepAsync(Guid id, string? output, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var applied = MutateIf(id,
            r => r.Status == StepStatus.InProgress,
            r =>
            {
                r.Status = StepStatus.Completed;
                r.Output = output;
                r.ClaimedAt = null;
                r.UpdatedAt = nowUtc;
            }) is not null;
        return Task.FromResult(applied);
    }

    // ── ScheduleRetryAsync ────────────────────────────────────────────────────

    public Task<bool> ScheduleRetryAsync(
        Guid id, DateTime nextRunAt, string error, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var nextRunUtc = DateTime.SpecifyKind(nextRunAt, DateTimeKind.Utc);
        var applied = MutateIf(id,
            r => r.Status == StepStatus.InProgress,
            r =>
            {
                r.Status = StepStatus.Pending;
                r.AttemptCount += 1;
                r.NextRunAt = nextRunUtc;
                r.ClaimedAt = null;
                r.LastError = error;
                r.UpdatedAt = nowUtc;
            }) is not null;
        return Task.FromResult(applied);
    }

    // ── CompensateStepAsync ───────────────────────────────────────────────────

    public Task<SagaStepRecord?> CompensateStepAsync(
        Guid id,
        string compensationStepName,
        string compensationIdempotencyKey,
        string compensationPayloadJson,
        string error,
        CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;

        // [0] Conditional transition of the failing forward step (single winner).
        var transitioned = MutateIf(id,
            r => r.Status == StepStatus.InProgress,
            r =>
            {
                r.Status = StepStatus.Compensating;
                r.AttemptCount += 1;
                r.ClaimedAt = null;
                r.LastError = error;
                r.UpdatedAt = nowUtc;
            });
        if (transitioned is null)
            return Task.FromResult<SagaStepRecord?>(null); // lost the race

        // [1] CREATE the declared compensation row as a fresh Pending.
        var compensation = new SagaStepRecord
        {
            Id = Guid.NewGuid(),
            SagaName = transitioned.SagaName,
            StepName = compensationStepName,
            CorrelationKey = transitioned.CorrelationKey,
            StepIdempotencyKey = compensationIdempotencyKey,
            Payload = compensationPayloadJson,
            Status = StepStatus.Pending,
            IsCompensation = true,
            AttemptCount = 0,
            NextRunAt = nowUtc,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
        };
        _steps[compensation.Id] = Copy(compensation);
        return Task.FromResult<SagaStepRecord?>(Copy(compensation));
    }

    // ── DeadLetterStepAsync ───────────────────────────────────────────────────

    public Task<bool> DeadLetterStepAsync(Guid id, string error, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var applied = MutateIf(id,
            r => r.Status == StepStatus.InProgress,
            r =>
            {
                r.Status = StepStatus.DeadLettered;
                r.DeadLettered = true;
                r.AttemptCount += 1;
                r.ClaimedAt = null;
                r.LastError = error;
                r.UpdatedAt = nowUtc;
            }) is not null;
        return Task.FromResult(applied);
    }

    // ── ParkStepAsync — suspend on signal/timer ───────────────────────────────

    public Task<bool> ParkStepAsync(
        Guid id, string gateId, DateTime? resumeAt, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;

        // Mirror SurrealSagaStore: park KIND is the single discriminator (no
        // sentinel). TIMER park (resumeAt set) ⇒ gate_id null + NextRunAt set;
        // SIGNAL park (resumeAt null) ⇒ gate_id set + NextRunAt LEFT UNCHANGED.
        var applied = MutateIf(id,
            r => r.Status == StepStatus.InProgress,
            r =>
            {
                r.Status = StepStatus.Parked;
                r.ClaimedAt = null;
                if (resumeAt.HasValue)
                {
                    r.GateId = null;
                    r.NextRunAt = DateTime.SpecifyKind(resumeAt.Value, DateTimeKind.Utc);
                }
                else
                {
                    r.GateId = gateId;
                    // NextRunAt untouched — a signal-only park is never timer-due
                    // and never claimed by the due scan (status is Parked).
                }
                r.UpdatedAt = nowUtc;
            }) is not null;
        return Task.FromResult(applied);
    }

    // ── TrySignalAsync — un-park a gate step (single-winner) ───────────────────

    public Task<SagaStepRecord?> TrySignalAsync(
        string correlationKey, string gateId, string? newPayloadJson, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;

        // Find the first Parked row matching correlation + gate, then apply the
        // conditional un-park GUARDED on it still being Parked (a duplicate/racing
        // signal sees it already Pending and mutates nothing → null).
        var candidate = _steps.Values
            .Where(r => r.Status == StepStatus.Parked
                        && r.CorrelationKey == correlationKey
                        && r.GateId == gateId)
            .OrderBy(r => r.NextRunAt)
            .Select(r => r.Id)
            .FirstOrDefault();

        if (candidate == Guid.Empty)
            return Task.FromResult<SagaStepRecord?>(null);

        var unparked = MutateIf(candidate,
            r => r.Status == StepStatus.Parked
                 && r.CorrelationKey == correlationKey
                 && r.GateId == gateId,
            r =>
            {
                r.Status = StepStatus.Pending;
                r.NextRunAt = nowUtc;
                r.GateId = null;
                if (newPayloadJson is not null)
                    r.Payload = newPayloadJson;
                r.UpdatedAt = nowUtc;
            });

        return Task.FromResult(unparked);
    }

    // ── GetParkedStepAsync — read parked row (no mutation) ─────────────────────

    public Task<SagaStepRecord?> GetParkedStepAsync(
        string correlationKey, string gateId, CancellationToken ct)
    {
        var parked = _steps.Values
            .Where(r => r.Status == StepStatus.Parked
                        && r.CorrelationKey == correlationKey
                        && r.GateId == gateId)
            .OrderBy(r => r.NextRunAt)
            .Select(Copy)
            .FirstOrDefault();
        return Task.FromResult<SagaStepRecord?>(parked);
    }

    // ── StepExistsAsync — idempotent-enqueue guard ────────────────────────────

    public Task<bool> StepExistsAsync(string correlationKey, string stepName, CancellationToken ct)
        => Task.FromResult(_steps.Values.Any(r =>
            r.CorrelationKey == correlationKey && r.StepName == stepName));

    // ── GetAsync ──────────────────────────────────────────────────────────────

    public Task<SagaStepRecord?> GetAsync(Guid id, CancellationToken ct)
        => Task.FromResult(_steps.TryGetValue(id, out var rec) ? Copy(rec) : null);

    // ── Operator / dead-letter surface (Phase-F) ──────────────────────────────

    public Task<IReadOnlyList<SagaStepRecord>> ListByStatusesAsync(
        IReadOnlyCollection<StepStatus> statuses, int limit, CancellationToken ct)
    {
        if (statuses.Count == 0)
            return Task.FromResult<IReadOnlyList<SagaStepRecord>>(Array.Empty<SagaStepRecord>());
        var safeLimit = Math.Clamp(limit, 1, 1000);
        var set = statuses.ToHashSet();
        var rows = _steps.Values
            .Where(r => set.Contains(r.Status))
            .OrderByDescending(r => r.UpdatedAt)
            .Take(safeLimit)
            .Select(Copy)
            .ToList();
        return Task.FromResult<IReadOnlyList<SagaStepRecord>>(rows);
    }

    public Task<bool> RequeueStepAsync(Guid id, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var applied = MutateIf(id,
            r => r.Status is StepStatus.Parked or StepStatus.DeadLettered,
            r =>
            {
                r.Status = StepStatus.Pending;
                r.NextRunAt = nowUtc;
                r.ClaimedAt = null;
                r.GateId = null;
                r.DeadLettered = false;
                r.UpdatedAt = nowUtc;
            }) is not null;
        return Task.FromResult(applied);
    }

    public Task<bool> CancelStepAsync(Guid id, string reason, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var applied = MutateIf(id,
            r => r.Status is StepStatus.Pending or StepStatus.Parked or StepStatus.DeadLettered,
            r =>
            {
                r.Status = StepStatus.Cancelled;
                r.ClaimedAt = null;
                r.GateId = null;
                r.LastError = reason;
                r.UpdatedAt = nowUtc;
            }) is not null;
        return Task.FromResult(applied);
    }

    // ── Test-only helpers (not part of ISagaStore) ────────────────────────────

    /// <summary>
    /// Defensive snapshot of every step record currently in the store, for test
    /// assertions. Each row is a deep copy so the caller cannot mutate the store.
    /// </summary>
    public IReadOnlyList<SagaStepRecord> Snapshot()
        => _steps.Values.Select(Copy).ToList();

    /// <summary>
    /// Collapse retry backoff DETERMINISTICALLY: pull every Pending row whose
    /// <see cref="SagaStepRecord.NextRunAt"/> is in the (real) future back to now,
    /// so the next due scan claims it immediately. <see cref="RetryPolicy"/>'s
    /// exponential+jitter backoff pushes a failed step's NextRunAt seconds ahead;
    /// a pump loop must not block on wall-clock time to exercise the
    /// retry→compensation path. Parked rows (gate/timer waits) are left untouched
    /// — only Pending retries are pulled forward. Returns how many rows moved.
    /// </summary>
    public int PullForwardPendingRetries()
    {
        var now = DateTime.UtcNow;
        var moved = 0;
        foreach (var rec in _steps.Values.ToList())
        {
            if (rec.Status == StepStatus.Pending
                && DateTime.SpecifyKind(rec.NextRunAt, DateTimeKind.Utc) > now)
            {
                if (MutateIf(rec.Id,
                        r => r.Status == StepStatus.Pending,
                        r => r.NextRunAt = now) is not null)
                    moved++;
            }
        }
        return moved;
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The in-memory analogue of SurrealDB's conditional <c>UPDATE … WHERE …
    /// RETURN AFTER</c>: atomically (per the ConcurrentDictionary update lambda)
    /// re-checks <paramref name="predicate"/> against the live row and applies
    /// <paramref name="mutate"/> only when it still holds, returning a defensive
    /// copy of the mutated row — or <c>null</c> when the predicate no longer held
    /// (lost the single-winner race / row absent). The predicate + mutation run
    /// inside <see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate"/>'s update
    /// delegate so concurrent callers serialize on the same key, giving the same
    /// at-most-one-winner property as the conditional UPDATE.
    /// </summary>
    private SagaStepRecord? MutateIf(
        Guid id, Func<SagaStepRecord, bool> predicate, Action<SagaStepRecord> mutate)
    {
        if (!_steps.ContainsKey(id))
            return null;

        SagaStepRecord? winner = null;
        _steps.AddOrUpdate(
            id,
            // Key vanished between the ContainsKey check and here — no add.
            _ => throw new InvalidOperationException(
                $"Saga step {id} disappeared during conditional update."),
            (_, current) =>
            {
                if (!predicate(current))
                    return current; // predicate failed ⇒ no-op, winner stays null

                var next = Copy(current);
                mutate(next);
                winner = Copy(next);
                return next;
            });

        return winner;
    }

    /// <summary>Field-by-field deep copy (SagaStepRecord has no Clone).</summary>
    private static SagaStepRecord Copy(SagaStepRecord r) => new()
    {
        Id                 = r.Id,
        CorrelationKey     = r.CorrelationKey,
        SagaName           = r.SagaName,
        StepName           = r.StepName,
        StepIdempotencyKey = r.StepIdempotencyKey,
        Payload            = r.Payload,
        Status             = r.Status,
        IsCompensation     = r.IsCompensation,
        AttemptCount       = r.AttemptCount,
        NextRunAt          = r.NextRunAt,
        ClaimedAt          = r.ClaimedAt,
        LastError          = r.LastError,
        Output             = r.Output,
        DeadLettered       = r.DeadLettered,
        GateId             = r.GateId,
        CreatedAt          = r.CreatedAt,
        UpdatedAt          = r.UpdatedAt,
    };
}
