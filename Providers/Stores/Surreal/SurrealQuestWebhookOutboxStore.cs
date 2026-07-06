using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IQuestWebhookOutboxStore"/> (final-hardening F3). The
/// transactional-outbox persistence for the GENERIC quest webhook bridge — a
/// column-for-column mirror of <see cref="SurrealConsentWebhookOutboxStore"/>: a row is
/// CREATEd, a polling worker scans due rows and transitions them with conditional
/// single-winner UPDATEs (<c>AffectedCount()==1</c> is the arbiter). Guid("N")
/// lowercase-hex record ids, record-link columns for tenant/run/node/quest.
///
/// <para><b>No-throw.</b> Every method captures exceptions into an
/// <see cref="AZOAResult{T}"/> rather than throwing — the worker logs + reschedules.</para>
/// </summary>
public sealed class SurrealQuestWebhookOutboxStore : IQuestWebhookOutboxStore
{
    private const string Table = "quest_webhook_event";

    // Status literals passed as BOUND parameters so the schema's ASSERT INSIDE [...]
    // compares against the same tokens (no token smuggling).
    private const string StatusPending      = "Pending";
    private const string StatusDelivered    = "Delivered";
    private const string StatusDeadLettered = "DeadLettered";

    private const int LastErrorMaxLength = 2048;

    private readonly ISurrealExecutor _executor;

    public SurrealQuestWebhookOutboxStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<AZOAResult<QuestWebhookEvent>> EnqueueAsync(QuestWebhookEvent evt, CancellationToken ct = default)
    {
        try
        {
            if (evt.Id == Guid.Empty) evt.Id = Guid.NewGuid();
            var poco = FromDomain(evt);

            var q = SurrealQuery
                .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t", Table)
                .WithParam("_id", poco.Id)
                .WithParam("_body", poco);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();
            var saved = resp.GetValues<QuestWebhookEventPoco>(0).FirstOrDefault();
            return new AZOAResult<QuestWebhookEvent>
            {
                Result = saved is not null ? ToDomain(saved) : evt,
                Message = "Enqueued.",
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<QuestWebhookEvent>().CaptureException(ex, $"SurrealQuestWebhookOutboxStore.EnqueueAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IReadOnlyList<QuestWebhookEvent>>> ListDueAsync(
        DateTime now, int limit, CancellationToken ct = default)
    {
        try
        {
            var safeLimit = Math.Clamp(limit, 1, 1000);
            var nowUtc = DateTime.SpecifyKind(now, DateTimeKind.Utc);

            var q = SurrealQuery
                .Of("SELECT * FROM quest_webhook_event WHERE status = $_pending AND next_attempt_at <= $_now ORDER BY next_attempt_at ASC LIMIT $_limit")
                .WithParam("_pending", StatusPending)
                .WithParam("_now", nowUtc)
                .WithParam("_limit", safeLimit);

            var rows = await _executor.QueryAsync<QuestWebhookEventPoco>(q, ct);
            IReadOnlyList<QuestWebhookEvent> result = rows.Select(ToDomain).ToList();
            return new AZOAResult<IReadOnlyList<QuestWebhookEvent>> { Result = result, Message = "Success" };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IReadOnlyList<QuestWebhookEvent>>().CaptureException(ex, $"SurrealQuestWebhookOutboxStore.ListDueAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> MarkDeliveredAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
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
            return new AZOAResult<bool>().CaptureException(ex, $"SurrealQuestWebhookOutboxStore.MarkDeliveredAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> RescheduleAsync(
        Guid id, int attemptCount, DateTime nextAttemptAt, string lastError, CancellationToken ct = default)
    {
        try
        {
            var nextUtc = DateTime.SpecifyKind(nextAttemptAt, DateTimeKind.Utc);
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
            return new AZOAResult<bool>().CaptureException(ex, $"SurrealQuestWebhookOutboxStore.RescheduleAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> DeadLetterAsync(Guid id, string lastError, CancellationToken ct = default)
    {
        try
        {
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
            return new AZOAResult<bool>().CaptureException(ex, $"SurrealQuestWebhookOutboxStore.DeadLetterAsync failed: {ex.Message}");
        }
    }

    // ── Mapping ────────────────────────────────────────────────────────────────

    private static string? Truncate(string? s, int max) =>
        s is null || s.Length <= max ? s : s[..max];

    private static QuestWebhookEventPoco FromDomain(QuestWebhookEvent e) => new()
    {
        Id            = SurrealId.ToSurrealId(e.Id),
        TenantId      = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(e.TenantId)) ?? string.Empty,
        EventType     = e.EventType ?? string.Empty,
        RunId         = SurrealLink.ToLink("quest_run", SurrealId.ToSurrealId(e.RunId)) ?? string.Empty,
        NodeId        = SurrealLink.ToLink("quest_node", SurrealId.ToSurrealId(e.NodeId)) ?? string.Empty,
        QuestId       = SurrealLink.ToLink("quest", SurrealId.ToSurrealId(e.QuestId)) ?? string.Empty,
        PayloadJson   = e.PayloadJson ?? "{}",
        OccurredAt    = new DateTimeOffset(DateTime.SpecifyKind(e.OccurredAt, DateTimeKind.Utc)),
        Status        = e.Status.ToString(),
        AttemptCount  = e.AttemptCount,
        NextAttemptAt = new DateTimeOffset(DateTime.SpecifyKind(e.NextAttemptAt, DateTimeKind.Utc)),
        LastError     = e.LastError,
        IdempotencyId = e.IdempotencyId,
        CreatedAt     = new DateTimeOffset(DateTime.SpecifyKind(e.CreatedAt, DateTimeKind.Utc)),
    };

    private static QuestWebhookEvent ToDomain(QuestWebhookEventPoco p) => new()
    {
        Id            = SurrealId.FromSurrealId(StripIdPrefix(p.Id)),
        TenantId      = SurrealId.FromSurrealId(SurrealLink.FromLink(p.TenantId)!),
        EventType     = p.EventType ?? string.Empty,
        RunId         = SurrealId.FromSurrealId(SurrealLink.FromLink(p.RunId)!),
        NodeId        = SurrealId.FromSurrealId(SurrealLink.FromLink(p.NodeId)!),
        QuestId       = SurrealId.FromSurrealId(SurrealLink.FromLink(p.QuestId)!),
        PayloadJson   = p.PayloadJson ?? "{}",
        OccurredAt    = p.OccurredAt.UtcDateTime,
        Status        = Enum.TryParse<QuestWebhookDeliveryStatus>(p.Status, ignoreCase: true, out var s) ? s : QuestWebhookDeliveryStatus.Pending,
        AttemptCount  = (int)p.AttemptCount,
        NextAttemptAt = p.NextAttemptAt.UtcDateTime,
        LastError     = p.LastError,
        IdempotencyId = p.IdempotencyId ?? string.Empty,
        CreatedAt     = p.CreatedAt.UtcDateTime,
    };

    /// <summary>Strips a leading "quest_webhook_event:" prefix if SurrealDB returned the
    /// id in thing-form rather than the bare suffix (mirrors SurrealConsentWebhookOutboxStore).</summary>
    private static string StripIdPrefix(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var colon = raw.IndexOf(':');
        return colon >= 0 && colon < raw.Length - 1 ? raw[(colon + 1)..] : raw;
    }

    // ── POCO (private; inline until source-gen catches up) ─────────────────────

    private sealed class QuestWebhookEventPoco : SurrealForge.Client.ISurrealRecord
    {
        public string SchemaName => Table;

        [JsonPropertyName("id")]              public string Id { get; set; } = string.Empty;
        [JsonPropertyName("tenant_id")]       public string TenantId { get; set; } = string.Empty;
        [JsonPropertyName("event_type")]      public string EventType { get; set; } = string.Empty;
        [JsonPropertyName("run_id")]          public string RunId { get; set; } = string.Empty;
        [JsonPropertyName("node_id")]         public string NodeId { get; set; } = string.Empty;
        [JsonPropertyName("quest_id")]        public string QuestId { get; set; } = string.Empty;
        [JsonPropertyName("payload_json")]    public string PayloadJson { get; set; } = "{}";
        [JsonPropertyName("occurred_at")]     public DateTimeOffset OccurredAt { get; set; }
        [JsonPropertyName("status")]          public string Status { get; set; } = "Pending";
        [JsonPropertyName("attempt_count")]   public long AttemptCount { get; set; }
        [JsonPropertyName("next_attempt_at")] public DateTimeOffset NextAttemptAt { get; set; }
        [JsonPropertyName("last_error")]      public string? LastError { get; set; }
        [JsonPropertyName("idempotency_id")]  public string IdempotencyId { get; set; } = string.Empty;
        [JsonPropertyName("created_at")]      public DateTimeOffset CreatedAt { get; set; }
    }
}
