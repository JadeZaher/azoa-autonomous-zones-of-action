// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the action_webhook_event table
// (final-hardening F3 — generic action.emit webhook transactional outbox).

#nullable enable

using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("action_webhook_event",
        Aggregate = "Immutable accepted action webhook event",
        Guardrail = "G6 SCHEMAFULL, immutable accepted event and versioned delivery")]
    [Slice("bridge")]
    [Index("action_webhook_event_due_scan", Fields = new[] { "status", "next_attempt_at" })]
    [Index("action_webhook_event_by_tenant", Fields = new[] { "tenant_id" })]
    public partial class ActionWebhookEvent : ISurrealRecord
    {
        public const string SchemaNameConst = "action_webhook_event";
        public string SchemaName => SchemaNameConst;

        public string? ReceiptId { get; set; }
        public string? RegistrationId { get; set; }
        public long? DestinationRevision { get; set; }
        public string? DestinationUrl { get; set; }
        public string? BodyJson { get; set; }
        [Default("0")] public long Version { get; set; }
        [Default("0")] public long RetryGeneration { get; set; }
        [Default("0")] public long CycleAttemptCount { get; set; }
        public string? LeaseId { get; set; }
        public DateTimeOffset? LeaseExpiresAt { get; set; }
        public long? AttemptSecretRevision { get; set; }
        public string? AttemptEncryptedSecret { get; set; }

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
        [References(typeof(User))]
        public string TenantId { get; set; } = string.Empty;

        [FieldGroup("Event payload — the tenant-defined event name (free-form; AZOA does not interpret it)")]
        [JsonPropertyName("event_type")]
        public string EventType { get; set; } = string.Empty;

        [FieldGroup("Event payload — the owning action_run")]
        [JsonPropertyName("run_id")]
        [References(typeof(ActionRun))]
        public string RunId { get; set; } = string.Empty;

        [FieldGroup("Event payload — the Emit definition node that fired the event")]
        [JsonPropertyName("node_id")]
        [References(typeof(ActionNode))]
        public string NodeId { get; set; } = string.Empty;

        [FieldGroup("Event payload — the action definition the run instantiated")]
        [JsonPropertyName("action_id")]
        [References(typeof(ActionDefinition))]
        public string ActionId { get; set; } = string.Empty;

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
