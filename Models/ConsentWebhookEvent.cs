// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models;

/// <summary>
/// Which consent lifecycle transition a <see cref="ConsentWebhookEvent"/> describes
/// (tenant-consent-delegation §4, AC7). These are the three outbound events ArdaNova's
/// no-blockchain orchestration subscribes to — <c>consent.granted</c> /
/// <c>consent.revoked</c> / <c>consent.expired</c>. The wire <c>eventType</c> string is
/// derived from this enum via <see cref="ConsentWebhookEvent.WireEventType"/>.
/// </summary>
public enum ConsentWebhookEventType
{
    /// <summary>A new grant was written (<c>consent.granted</c>).</summary>
    Granted = 0,

    /// <summary>A grant was revoked (<c>consent.revoked</c>) — observe-only; the seam
    /// already treats the grant as dead the instant <c>RevokedAt</c> is written (AC8).</summary>
    Revoked = 1,

    /// <summary>A grant lapsed past its <c>ExpiresAt</c> (<c>consent.expired</c>) — a
    /// NOTIFICATION only; expiry is enforced live at the signing seam, NOT by this
    /// event (spec §3, M4).</summary>
    Expired = 2,
}

/// <summary>
/// Delivery lifecycle of a <see cref="ConsentWebhookEvent"/> outbox row
/// (tenant-consent-delegation §4, AC7). Drives the delivery worker's due-scan +
/// single-winner conditional updates — the SAME transactional-outbox discipline as
/// the saga step processor (<c>SagaSteps.StepStatus</c>).
/// </summary>
public enum ConsentWebhookDeliveryStatus
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
/// The transactional-outbox event domain model for the consent webhook bridge
/// (tenant-consent-delegation §4, AC7 — NEW outbound infra built ON the saga outbox
/// pattern). One row == one consent lifecycle event owed to exactly one tenant.
///
/// <para><b>Transactional outbox (no dual-write).</b> A row is enqueued in the SAME
/// logical transaction as the consent state change (the grant/revoke Upsert) — see
/// <c>IConsentWebhookOutboxStore.EnqueueAsync</c>. A polling delivery worker then claims
/// due rows and POSTs them with retry + idempotency id. AZOA never makes an outbound
/// HTTP call inside the request that mutated the grant; the request only writes the
/// outbox row, exactly like the saga step processor.</para>
///
/// <para><b>Observe-only boundary (AC8).</b> This event is a NOTIFICATION. The signing
/// seam is the enforcement path: a revoked grant is dead the instant <c>RevokedAt</c> is
/// written, independent of whether — or when — this event is delivered. ArdaNova can
/// only react to the event, never override the AZOA decision it describes.</para>
/// </summary>
public sealed class ConsentWebhookEvent
{
    /// <summary>Outbox row id (the durable record key).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The tenant this event is owed to. STRICT per-tenant isolation (H5):
    /// the delivery worker resolves ONLY this tenant's registration + secret; a tenant
    /// never receives another tenant's events.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Which lifecycle transition this describes.</summary>
    public ConsentWebhookEventType EventType { get; set; }

    /// <summary>The grant the event is about (payload <c>grantId</c>).</summary>
    public Guid GrantId { get; set; }

    /// <summary>The grantor user's avatar (payload <c>avatarId</c>).</summary>
    public Guid AvatarId { get; set; }

    /// <summary>The grant's scopes as a comma-separated string (payload <c>scopes</c>).
    /// Stored as CSV to mirror the <c>ConsentGrant</c> store's scope serialization.</summary>
    public string Scopes { get; set; } = string.Empty;

    /// <summary>The opaque ArdaNova participation id, when the grant carried one
    /// (payload <c>participationRef</c>); null for <c>UserExplicit</c> grants.</summary>
    public string? ParticipationRef { get; set; }

    /// <summary>When the described consent transition occurred (UTC) — the payload
    /// <c>occurredAt</c>. This is the business event time, distinct from the delivery
    /// <see cref="X_AzoaTimestamp_Doc"/> wall-clock used by the HMAC freshness window.</summary>
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    // ── Delivery tracking ──────────────────────────────────────────────────────

    /// <summary>Delivery lifecycle state — drives the worker's due-scan + conditional
    /// transitions.</summary>
    public ConsentWebhookDeliveryStatus Status { get; set; } = ConsentWebhookDeliveryStatus.Pending;

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
    /// header) so ArdaNova can dedup a redelivered event. Constant across all retries of
    /// THIS event — a retried POST carries the same idempotency id, so a receiver that
    /// already applied it is a no-op.</summary>
    public string IdempotencyId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>When the outbox row was created (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The wire <c>eventType</c> string for the POST body — <c>consent.granted</c> /
    /// <c>consent.revoked</c> / <c>consent.expired</c>. Derived from
    /// <see cref="EventType"/> so the domain enum stays the single source of truth.
    /// </summary>
    public string WireEventType => EventType switch
    {
        ConsentWebhookEventType.Granted => "consent.granted",
        ConsentWebhookEventType.Revoked => "consent.revoked",
        ConsentWebhookEventType.Expired => "consent.expired",
        _ => "consent.unknown",
    };

    /// <summary>Documentation anchor only — see the delivery worker for the
    /// <c>X-Azoa-Timestamp</c> header semantics (the HMAC-signed delivery timestamp,
    /// distinct from <see cref="OccurredAt"/>).</summary>
    internal const string X_AzoaTimestamp_Doc = "X-Azoa-Timestamp";
}
