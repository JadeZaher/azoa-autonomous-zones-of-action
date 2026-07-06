// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models;

/// <summary>
/// Delivery lifecycle of a <see cref="QuestWebhookEvent"/> outbox row
/// (final-hardening F3). Drives the delivery worker's due-scan + single-winner
/// conditional updates — the SAME transactional-outbox discipline as the consent
/// webhook outbox (<c>ConsentWebhookDeliveryStatus</c>) and the saga step processor.
/// </summary>
public enum QuestWebhookDeliveryStatus
{
    /// <summary>Enqueued, awaiting (or between) delivery attempts. The due-scan claims
    /// rows in this state whose <c>NextAttemptAt</c> has passed.</summary>
    Pending = 0,

    /// <summary>Receiver returned 2xx — terminal success.</summary>
    Delivered = 1,

    /// <summary>Exhausted the retry budget (or permanently undeliverable — e.g. no
    /// active registration, SSRF-blocked URL) — terminal failure. NEVER retried.</summary>
    DeadLettered = 2,
}

/// <summary>
/// The transactional-outbox event domain model for the GENERIC quest webhook bridge
/// (final-hardening F3 — generalizes the shipped consent outbox to arbitrary
/// <c>quest.emit</c> events). One row == one tenant-defined event fired by an
/// <c>Emit</c> quest node, owed to exactly one tenant.
///
/// <para><b>Transactional outbox (no dual-write).</b> A row is enqueued by the
/// <c>Emit</c> node handler in the SAME logical write as the node's execution output —
/// see <c>IQuestWebhookOutboxStore.EnqueueAsync</c>. A polling delivery worker
/// (<c>QuestWebhookDeliveryWorker</c>) then claims due rows and POSTs them with
/// retry + a stable idempotency id + a timestamped per-tenant HMAC. AZOA never makes
/// an outbound HTTP call inside the quest-run request; the run only writes the outbox
/// row, exactly like the consent outbox + the saga step processor.</para>
///
/// <para><b>Strict per-tenant isolation (H5).</b> <see cref="TenantId"/> scopes every
/// delivery to ONLY that tenant's <c>WebhookRegistration</c> + secret — the SAME
/// registration table the consent path uses, so a tenant configures ONE endpoint that
/// receives both its consent events and its quest.emit events, each signed with its own
/// secret. A tenant never receives another tenant's events.</para>
///
/// <para><b>Best-effort notification.</b> The event is a NOTIFICATION only — a failed
/// enqueue or delivery never changes quest-run state. The <c>Emit</c> node's output
/// (the serialized payload on <c>QuestNodeExecution.Output</c>) remains the tenant's
/// authoritative settlement surface; the webhook is a convenience push on top.</para>
/// </summary>
public sealed class QuestWebhookEvent
{
    /// <summary>Outbox row id (the durable record key).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The tenant this event is owed to. STRICT per-tenant isolation (H5):
    /// the delivery worker resolves ONLY this tenant's registration + secret.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The tenant-defined event name (payload <c>eventType</c>), e.g.
    /// <c>fractionalization.settled</c>. Free-form — AZOA does not interpret it; it is
    /// echoed to the receiver so the tenant can route on it. Defaults to
    /// <c>quest.emit</c> when the node config supplies none.</summary>
    public string EventType { get; set; } = "quest.emit";

    /// <summary>The owning <c>QuestRun</c> the emitting node belongs to (payload
    /// <c>runId</c>). Lets the receiver correlate the event to a run.</summary>
    public Guid RunId { get; set; }

    /// <summary>The <c>Emit</c> definition node id that fired the event (payload
    /// <c>nodeId</c>).</summary>
    public Guid NodeId { get; set; }

    /// <summary>The quest definition id the run instantiated (payload <c>questId</c>).</summary>
    public Guid QuestId { get; set; }

    /// <summary>The opaque tenant-shaped payload the <c>Emit</c> node carried, serialized
    /// as a JSON string (payload <c>payload</c>). AZOA holds no settlement/fiat/payout
    /// state — this is the tenant's own data echoed back verbatim.</summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>When the emit occurred (UTC) — the payload <c>occurredAt</c>. Business
    /// event time, distinct from the delivery wall-clock the HMAC freshness window uses.</summary>
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    // ── Delivery tracking (mirrors ConsentWebhookEvent) ──────────────────────────

    /// <summary>Delivery lifecycle state — drives the worker's due-scan + conditional
    /// transitions.</summary>
    public QuestWebhookDeliveryStatus Status { get; set; } = QuestWebhookDeliveryStatus.Pending;

    /// <summary>Delivery attempts consumed so far. Bumped on each failed POST;
    /// dead-letters once it reaches the worker's configured max.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Earliest UTC time the worker may (re)attempt delivery. Pushed out by
    /// exponential backoff on each failure; the due-scan selects rows where this has
    /// passed.</summary>
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last delivery error (HTTP status / exception text), for diagnostics.</summary>
    public string? LastError { get; set; }

    /// <summary>A stable per-event id sent to the receiver (<c>X-Azoa-Idempotency-Id</c>
    /// header) so the tenant can dedup a redelivered event. Constant across all retries
    /// of THIS event.</summary>
    public string IdempotencyId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>When the outbox row was created (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
