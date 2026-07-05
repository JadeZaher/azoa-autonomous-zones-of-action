using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IConsentWebhookOutboxStore"/> (tenant-consent-delegation
/// §4, AC7). The transactional-outbox persistence for the consent webhook bridge —
/// mirrors the saga outbox store (<c>SurrealSagaStore</c>) SHAPE: a row is CREATEd, a
/// polling worker scans due rows and transitions them with conditional single-winner
/// UPDATEs (<c>AffectedCount()==1</c> is the arbiter, the same discipline as the saga
/// step claim). Inline-POCO style of <see cref="SurrealConsentGrantStore"/>:
/// Guid("N") lowercase-hex record ids, record-link columns for tenant/grant/avatar,
/// scopes stored as a CSV string.
///
/// <para><b>No dual-write (AC7).</b> <see cref="EnqueueAsync"/> is called by
/// ConsentManager right after the grant Upsert, in the same logical transaction — the
/// outbox row and the grant state change land together; delivery happens later out of
/// band.</para>
///
/// <para><b>No-throw.</b> Every method captures exceptions into an
/// <see cref="AZOAResult{T}"/> rather than throwing — the worker logs + reschedules.</para>
/// </summary>
public sealed class SurrealConsentWebhookOutboxStore : IConsentWebhookOutboxStore
{
    private const string Table = "consent_webhook_event";

    // Status literals are passed as BOUND parameters so the schema's
    // ASSERT INSIDE [...] compares against the same tokens (no token smuggling).
    private const string StatusPending      = "Pending";
    private const string StatusDelivered    = "Delivered";
    private const string StatusDeadLettered = "DeadLettered";

    private const int LastErrorMaxLength = 2048;

    private readonly ISurrealExecutor _executor;

    public SurrealConsentWebhookOutboxStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<AZOAResult<ConsentWebhookEvent>> EnqueueAsync(ConsentWebhookEvent evt, CancellationToken ct = default)
    {
        try
        {
            if (evt.Id == Guid.Empty) evt.Id = Guid.NewGuid();
            var poco = FromDomain(evt);

            // CREATE type::record('consent_webhook_event', <id>) CONTENT <body> RETURN
            // AFTER. type::record() builds the record id from a parameterized table+id
            // pair so the table identifier cannot be smuggled (G3 — no interpolation).
            var q = SurrealQuery
                .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t", Table)
                .WithParam("_id", poco.Id)
                .WithParam("_body", poco);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();
            var saved = resp.GetValues<ConsentWebhookEventPoco>(0).FirstOrDefault();
            return new AZOAResult<ConsentWebhookEvent>
            {
                Result = saved is not null ? ToDomain(saved) : evt,
                Message = "Enqueued.",
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<ConsentWebhookEvent>().CaptureException(ex, $"SurrealConsentWebhookOutboxStore.EnqueueAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IReadOnlyList<ConsentWebhookEvent>>> ListDueAsync(
        DateTime now, int limit, CancellationToken ct = default)
    {
        try
        {
            var safeLimit = Math.Clamp(limit, 1, 1000);
            var nowUtc = DateTime.SpecifyKind(now, DateTimeKind.Utc);

            // Due-scan: Pending rows whose next_attempt_at has passed, oldest first.
            // next_attempt_at is included in the projection because SurrealDB 3.x
            // requires the ORDER BY idiom to appear in the SELECT projection.
            var q = SurrealQuery
                .Of("SELECT * FROM consent_webhook_event WHERE status = $_pending AND next_attempt_at <= $_now ORDER BY next_attempt_at ASC LIMIT $_limit")
                .WithParam("_pending", StatusPending)
                .WithParam("_now", nowUtc)
                .WithParam("_limit", safeLimit);

            var rows = await _executor.QueryAsync<ConsentWebhookEventPoco>(q, ct);
            IReadOnlyList<ConsentWebhookEvent> result = rows.Select(ToDomain).ToList();
            return new AZOAResult<IReadOnlyList<ConsentWebhookEvent>> { Result = result, Message = "Success" };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IReadOnlyList<ConsentWebhookEvent>>().CaptureException(ex, $"SurrealConsentWebhookOutboxStore.ListDueAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> MarkDeliveredAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var nowUtc = DateTime.UtcNow;
            // Conditional single-winner: only a still-Pending row transitions. A
            // concurrent transition already moved it ⇒ AffectedCount==0 ⇒ false.
            var q = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) SET status = $_delivered, last_error = NONE WHERE status = $_pending RETURN AFTER")
                .WithParam("_t", Table)
                .WithParam("_id", SurrealId.ToSurrealId(id))
                .WithParam("_delivered", StatusDelivered)
                .WithParam("_pending", StatusPending);

            var resp = await _executor.ExecuteAsync(q, ct);
            if (resp.Count == 0 || !resp[0].IsOk)
                return new AZOAResult<bool> { Result = false, Message = "No-op." };
            return new AZOAResult<bool> { Result = resp[0].AffectedCount() == 1, Message = "Success" };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex, $"SurrealConsentWebhookOutboxStore.MarkDeliveredAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> RescheduleAsync(
        Guid id, int attemptCount, DateTime nextAttemptAt, string lastError, CancellationToken ct = default)
    {
        try
        {
            var nextUtc = DateTime.SpecifyKind(nextAttemptAt, DateTimeKind.Utc);
            // Conditional on Pending — the row stays Pending (a retry), bumps the
            // attempt count, pushes next_attempt_at out by the worker's backoff, and
            // records the error. Single-winner: a row that left Pending no-ops.
            var q = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) SET attempt_count = $_attempts, next_attempt_at = $_next, last_error = $_error WHERE status = $_pending RETURN AFTER")
                .WithParam("_t", Table)
                .WithParam("_id", SurrealId.ToSurrealId(id))
                .WithParam("_attempts", attemptCount)
                .WithParam("_next", nextUtc)
                .WithParam("_error", (object?)Truncate(lastError, LastErrorMaxLength)!)
                .WithParam("_pending", StatusPending);

            var resp = await _executor.ExecuteAsync(q, ct);
            if (resp.Count == 0 || !resp[0].IsOk)
                return new AZOAResult<bool> { Result = false, Message = "No-op." };
            return new AZOAResult<bool> { Result = resp[0].AffectedCount() == 1, Message = "Success" };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex, $"SurrealConsentWebhookOutboxStore.RescheduleAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> DeadLetterAsync(Guid id, string lastError, CancellationToken ct = default)
    {
        try
        {
            // Terminal: conditional on Pending ⇒ DeadLettered, record the reason.
            // Never retried after this. Single-winner.
            var q = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) SET status = $_dead, last_error = $_error WHERE status = $_pending RETURN AFTER")
                .WithParam("_t", Table)
                .WithParam("_id", SurrealId.ToSurrealId(id))
                .WithParam("_dead", StatusDeadLettered)
                .WithParam("_error", (object?)Truncate(lastError, LastErrorMaxLength)!)
                .WithParam("_pending", StatusPending);

            var resp = await _executor.ExecuteAsync(q, ct);
            if (resp.Count == 0 || !resp[0].IsOk)
                return new AZOAResult<bool> { Result = false, Message = "No-op." };
            return new AZOAResult<bool> { Result = resp[0].AffectedCount() == 1, Message = "Success" };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex, $"SurrealConsentWebhookOutboxStore.DeadLetterAsync failed: {ex.Message}");
        }
    }

    // ── Mapping ────────────────────────────────────────────────────────────────


    private static string? Truncate(string? s, int max) =>
        s is null || s.Length <= max ? s : s[..max];

    private static ConsentWebhookEventPoco FromDomain(ConsentWebhookEvent e) => new()
    {
        Id               = SurrealId.ToSurrealId(e.Id),
        TenantId         = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(e.TenantId)) ?? string.Empty,
        EventType        = e.EventType.ToString(),
        GrantId          = SurrealLink.ToLink("consent_grant", SurrealId.ToSurrealId(e.GrantId)) ?? string.Empty,
        AvatarId         = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(e.AvatarId)) ?? string.Empty,
        Scopes           = e.Scopes ?? string.Empty,
        ParticipationRef = e.ParticipationRef,
        OccurredAt       = new DateTimeOffset(DateTime.SpecifyKind(e.OccurredAt, DateTimeKind.Utc)),
        Status           = e.Status.ToString(),
        AttemptCount     = e.AttemptCount,
        NextAttemptAt    = new DateTimeOffset(DateTime.SpecifyKind(e.NextAttemptAt, DateTimeKind.Utc)),
        LastError        = e.LastError,
        IdempotencyId    = e.IdempotencyId,
        CreatedAt        = new DateTimeOffset(DateTime.SpecifyKind(e.CreatedAt, DateTimeKind.Utc)),
    };

    private static ConsentWebhookEvent ToDomain(ConsentWebhookEventPoco p) => new()
    {
        Id               = SurrealId.FromSurrealId(StripIdPrefix(p.Id)),
        TenantId         = SurrealId.FromSurrealId(SurrealLink.FromLink(p.TenantId)!),
        EventType        = Enum.TryParse<ConsentWebhookEventType>(p.EventType, ignoreCase: true, out var t) ? t : ConsentWebhookEventType.Granted,
        GrantId          = SurrealId.FromSurrealId(SurrealLink.FromLink(p.GrantId)!),
        AvatarId         = SurrealId.FromSurrealId(SurrealLink.FromLink(p.AvatarId)!),
        Scopes           = p.Scopes ?? string.Empty,
        ParticipationRef = p.ParticipationRef,
        OccurredAt       = p.OccurredAt.UtcDateTime,
        Status           = Enum.TryParse<ConsentWebhookDeliveryStatus>(p.Status, ignoreCase: true, out var s) ? s : ConsentWebhookDeliveryStatus.Pending,
        AttemptCount     = (int)p.AttemptCount,
        NextAttemptAt    = p.NextAttemptAt.UtcDateTime,
        LastError        = p.LastError,
        IdempotencyId    = p.IdempotencyId ?? string.Empty,
        CreatedAt        = p.CreatedAt.UtcDateTime,
    };

    /// <summary>
    /// Strips a leading "consent_webhook_event:" prefix if SurrealDB returned the id in
    /// thing-form rather than the bare suffix. Defensive — both shapes appear depending
    /// on the serializer codepath (mirrors SurrealSagaStore.StripIdPrefix).
    /// </summary>
    private static string StripIdPrefix(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var colon = raw.IndexOf(':');
        return colon >= 0 && colon < raw.Length - 1 ? raw[(colon + 1)..] : raw;
    }

    // ── POCO (private; inline until source-gen catches up) ─────────────────────

    private sealed class ConsentWebhookEventPoco : SurrealForge.Client.ISurrealRecord
    {
        public string SchemaName => Table;

        [JsonPropertyName("id")]                public string Id { get; set; } = string.Empty;
        [JsonPropertyName("tenant_id")]         public string TenantId { get; set; } = string.Empty;
        [JsonPropertyName("event_type")]        public string EventType { get; set; } = "Granted";
        [JsonPropertyName("grant_id")]          public string GrantId { get; set; } = string.Empty;
        [JsonPropertyName("avatar_id")]         public string AvatarId { get; set; } = string.Empty;
        [JsonPropertyName("scopes")]            public string Scopes { get; set; } = string.Empty;
        [JsonPropertyName("participation_ref")] public string? ParticipationRef { get; set; }
        [JsonPropertyName("occurred_at")]       public DateTimeOffset OccurredAt { get; set; }
        [JsonPropertyName("status")]            public string Status { get; set; } = "Pending";
        [JsonPropertyName("attempt_count")]     public long AttemptCount { get; set; }
        [JsonPropertyName("next_attempt_at")]   public DateTimeOffset NextAttemptAt { get; set; }
        [JsonPropertyName("last_error")]        public string? LastError { get; set; }
        [JsonPropertyName("idempotency_id")]    public string IdempotencyId { get; set; } = string.Empty;
        [JsonPropertyName("created_at")]        public DateTimeOffset CreatedAt { get; set; }
    }
}
