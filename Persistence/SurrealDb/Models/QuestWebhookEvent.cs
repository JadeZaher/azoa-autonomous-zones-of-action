// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the quest_webhook_event table
// (final-hardening F3 — generic quest.emit webhook transactional outbox).

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("quest_webhook_event",
        Aggregate = "QuestWebhookEvent (Models/QuestWebhookEvent.cs)",
        Guardrail = "G6 SCHEMAFULL, transactional-outbox single-winner due-scan")]
    [SurrealNote("Generic quest.emit webhook transactional outbox (final-hardening F3). Generalizes the consent_webhook_event outbox to arbitrary tenant-defined events fired by an Emit quest node. One row == one quest.emit event owed to exactly one tenant. Written in the SAME logical write as the Emit node output (no dual-write); a polling delivery worker claims due rows and POSTs them with retry + idempotency id. Mirrors the consent_webhook_event outbox SHAPE exactly.")]
    [SurrealNote("Due-scan: status='Pending' AND next_attempt_at<=now, oldest first. Conditional transitions (MarkDelivered/Reschedule/DeadLetter) assert AffectedCount==1 — the same single-winner discipline as the saga step claim, so behaviour is safe under one worker (and identical across engines).")]
    [SurrealNote("BEST-EFFORT (notification): this row is a NOTIFICATION. Delivery never writes back to quest_run or quest_node_execution. The Emit node's serialized Output remains the tenant's authoritative settlement surface; the webhook is a convenience push.")]
    [SurrealNote("STRICT per-tenant isolation (H5): tenant_id scopes every delivery to ONLY that tenant's webhook_registration + secret (the SAME registration table the consent path uses). idempotency_id is the stable per-event dedup id sent to the receiver (X-Azoa-Idempotency-Id), constant across all retries of this event.")]
    [Slice("bridge")]
    [Index("quest_webhook_event_due_scan", Fields = new[] { "status", "next_attempt_at" })]
    [Index("quest_webhook_event_by_tenant", Fields = new[] { "tenant_id" })]
    public partial class QuestWebhookEvent : ISurrealRecord
    {
        public const string SchemaNameConst = "quest_webhook_event";
        public string SchemaName => SchemaNameConst;

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

        [FieldGroup("Event payload — the tenant-defined event name (free-form; AZOA does not interpret it)")]
        [JsonPropertyName("event_type")]
        public string EventType { get; set; } = string.Empty;

        [FieldGroup("Event payload — the owning quest_run")]
        [JsonPropertyName("run_id")]
        [References(typeof(QuestRun))]
        public string RunId { get; set; } = string.Empty;

        [FieldGroup("Event payload — the Emit definition node that fired the event")]
        [JsonPropertyName("node_id")]
        [References(typeof(QuestNode))]
        public string NodeId { get; set; } = string.Empty;

        [FieldGroup("Event payload — the quest definition the run instantiated")]
        [JsonPropertyName("quest_id")]
        [References(typeof(Quest))]
        public string QuestId { get; set; } = string.Empty;

        [FieldGroup("Event payload — the opaque tenant-shaped payload the Emit node carried (JSON string, echoed verbatim)")]
        [JsonPropertyName("payload_json")]
        public string PayloadJson { get; set; } = string.Empty;

        [FieldGroup("Event payload — when the emit occurred (business event time, distinct from the delivery timestamp)")]
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
