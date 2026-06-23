// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the consent_webhook_event table
// (tenant-consent-delegation §4, AC7 — outbound webhook transactional outbox).

#nullable enable

using System;
using System.Text.Json.Serialization;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("consent_webhook_event",
        Aggregate = "ConsentWebhookEvent (Models/ConsentWebhookEvent.cs)",
        Guardrail = "G6 SCHEMAFULL, transactional-outbox single-winner due-scan")]
    [SurrealNote("Outbound consent-webhook transactional outbox (tenant-consent-delegation §4, AC7). One row == one consent lifecycle event (granted/revoked/expired) owed to exactly one tenant. Written in the SAME logical transaction as the consent state change (no dual-write); a polling delivery worker claims due rows and POSTs them with retry + idempotency id. Mirrors the saga_steps outbox SHAPE exactly.")]
    [SurrealNote("Due-scan: status='Pending' AND next_attempt_at<=now, oldest first. Conditional transitions (MarkDelivered/Reschedule/DeadLetter) assert AffectedCount==1 — the same single-winner discipline as the saga step claim, so behaviour is safe under one worker (and identical across engines).")]
    [SurrealNote("OBSERVE-ONLY (AC8): this row is a NOTIFICATION. The signing seam is the enforcement path — a revoked grant is dead the instant revoked_at is written, independent of whether/when this event delivers. Delivery never writes back to consent_grant.")]
    [SurrealNote("STRICT per-tenant isolation (H5): tenant_id scopes every delivery to ONLY that tenant's webhook_registration + secret. idempotency_id is the stable per-event dedup id sent to the receiver (X-Azoa-Idempotency-Id), constant across all retries of this event.")]
    [Slice("bridge")]
    [Index("consent_webhook_event_due_scan", Fields = new[] { "status", "next_attempt_at" })]
    [Index("consent_webhook_event_by_tenant", Fields = new[] { "tenant_id" })]
    public partial class ConsentWebhookEvent : ISurrealRecord
    {
        public const string SchemaNameConst = "consent_webhook_event";
        public string SchemaName => SchemaNameConst;

        public enum WebhookEventType
        {
            Granted,
            Revoked,
            Expired,
        }

        public enum DeliveryStatus
        {
            Pending,
            Delivered,
            DeadLettered,
        }

        [Id]
        [FieldGroup("Core identity")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Owning tenant — the per-tenant isolation key (H5). Resolves ONLY this tenant's registration + secret at delivery.")]
        [JsonPropertyName("tenant_id")]
        [References(typeof(Avatar))]
        public string TenantId { get; set; } = string.Empty;

        [FieldGroup("Which consent lifecycle transition this describes")]
        [JsonPropertyName("event_type")]
        [Inside("Granted", "Revoked", "Expired")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public WebhookEventType EventType { get; set; }

        [FieldGroup("Event payload — the consent_grant this event is about")]
        [JsonPropertyName("grant_id")]
        [References(typeof(ConsentGrant))]
        public string GrantId { get; set; } = string.Empty;

        [FieldGroup("Event payload — the grantor user's avatar")]
        [JsonPropertyName("avatar_id")]
        [References(typeof(Avatar))]
        public string AvatarId { get; set; } = string.Empty;

        [FieldGroup("Event payload — scopes (CSV, mirrors consent_grant.scopes)")]
        [JsonPropertyName("scopes")]
        public string Scopes { get; set; } = string.Empty;

        [FieldGroup("Event payload — opaque ArdaNova participation id (NONE for UserExplicit grants)")]
        [JsonPropertyName("participation_ref")]
        public string? ParticipationRef { get; set; }

        [FieldGroup("Event payload — when the consent transition occurred (business event time, distinct from the delivery timestamp)")]
        [JsonPropertyName("occurred_at")]
        public DateTimeOffset OccurredAt { get; set; }

        // ── Delivery tracking ──────────────────────────────────────────────────

        [FieldGroup("Delivery lifecycle — drives the due-scan + conditional transitions")]
        [JsonPropertyName("status")]
        [Inside("Pending", "Delivered", "DeadLettered")]
        [Default("\"Pending\"")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DeliveryStatus Status { get; set; }

        [FieldGroup("Delivery attempts consumed so far")]
        [JsonPropertyName("attempt_count")]
        [Default("0")]
        public long AttemptCount { get; set; }

        [FieldGroup("Earliest UTC time the worker may (re)attempt delivery — pushed out by exponential backoff on failure; the due-scan selects rows where this has passed")]
        [JsonPropertyName("next_attempt_at")]
        public DateTimeOffset NextAttemptAt { get; set; }

        [FieldGroup("Last delivery error (HTTP status / exception text)")]
        [JsonPropertyName("last_error")]
        public string? LastError { get; set; }

        [FieldGroup("Stable per-event dedup id sent to the receiver (X-Azoa-Idempotency-Id), constant across all retries of THIS event")]
        [JsonPropertyName("idempotency_id")]
        [Required(NotEmpty = true)]
        public string IdempotencyId { get; set; } = string.Empty;

        [FieldGroup("Timestamps")]
        [JsonPropertyName("created_at")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }
    }
}
