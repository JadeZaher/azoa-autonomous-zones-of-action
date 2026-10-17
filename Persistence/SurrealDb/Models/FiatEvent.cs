// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the fiat_event table (W4.1 — the Fiat.USD webhook inbox).

#nullable enable

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("fiat_event",
        Aggregate = "FiatRail (Services/Fiat/StripeWebhookInbox.cs)",
        Guardrail = "G6 SCHEMAFULL; one row per provider event id and it is the dedup anchor")]
    [SurrealNote("Idempotent inbox for signature-verified payment-rail webhooks. One row is ONE provider event. The record id is the SHA-256 of provider|provider_event_id, so the CREATE itself de-duplicates a redelivery before any handler runs; the unique index is the second line of the same defence.")]
    [SurrealNote("The row carries NO amount, currency, customer or card field, and never the raw body -- only payload_digest. Every figure a handler acts on is re-read from the provider server-side, so a webhook body physically cannot be the source of a credited amount. That is the retired sidecar's pricing invariant expressed as a schema, not as a review note.")]
    [SurrealNote("status is the delivery disposition, not the payment's: Received (stored, no handler finished), Applied (a handler completed the work), Ignored (no handler claims this event type), Failed (a handler failed and the provider should retry). An issuance that is submitted but not yet confirmed stays Received -- marking it Applied is what would make a stuck credit invisible.")]
    [SurrealNote("environment and livemode are recorded together on purpose. The webhook endpoint refuses a delivery whose livemode disagrees with the bound rail environment, so a test-mode event can never settle live value; keeping both on the row makes that refusal auditable after the fact.")]
    [SurrealNote("intent_id is the funding intent (hosted_session, flow_kind='fiat-checkout') this event concerns, recovered from client_reference_id or provider metadata. It is option<> because reversal events can arrive for a charge whose session correlation was lost, and an unmatched event must still be recorded rather than dropped.")]
    [Slice("identity")]
    [Index("fiat_event_provider_event",
        Fields = new[] { "provider", "provider_event_id" },
        Unique = true)]
    [Index("fiat_event_intent", Fields = new[] { "intent_id", "status" })]
    [Index("fiat_event_type", Fields = new[] { "event_type", "status" })]
    [Index("fiat_event_charge", Fields = new[] { "charge_id" })]
    public partial class FiatEvent : ISurrealRecord
    {
        public const string SchemaNameConst = "fiat_event";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1)]
        [FieldGroup("Core identity (record id is the SHA-256 of provider|provider event id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        /// <summary>Payment provider that signed this delivery.</summary>
        [Column(Order = 2)]
        [Required(NotEmpty = true)]
        public string Provider { get; set; } = string.Empty;

        /// <summary>Provider's own event id; the dedup key a redelivery repeats.</summary>
        [Column(Order = 3)]
        [Required(NotEmpty = true)]
        public string ProviderEventId { get; set; } = string.Empty;

        /// <summary>Provider event discriminator, e.g. <c>checkout.session.completed</c>.</summary>
        [Column(Order = 4)]
        [Required(NotEmpty = true)]
        public string EventType { get; set; } = string.Empty;

        /// <summary>Provider API version the delivery was shaped by, when it names one.</summary>
        [Column(Order = 5)]
        public string? ApiVersion { get; set; }

        /// <summary>Rail environment the delivery was accepted in.</summary>
        [Column(Order = 6)]
        [FieldGroup("Environment (a test-mode event must never settle live value)")]
        [Inside("test", "live")]
        public string Environment { get; set; } = string.Empty;

        /// <summary>The provider's own live/test flag, as it arrived.</summary>
        [Column(Order = 7)]
        [Default("false")]
        public bool Livemode { get; set; }

        /// <summary>Delivery disposition; see the table notes.</summary>
        [Column(Order = 8)]
        [FieldGroup("Delivery disposition")]
        [Inside("Received", "Applied", "Ignored", "Failed")]
        [Default("\"Received\"")]
        public string Status { get; set; } = "Received";

        /// <summary>Attempts consumed, including this one.</summary>
        [Column(Order = 9)]
        [Default("0")]
        public long AttemptCount { get; set; }

        /// <summary>Why the last attempt failed. Never provider text a consumer would see.</summary>
        [Column(Order = 10)]
        public string? LastError { get; set; }

        /// <summary>The funding intent this event concerns, when it could be correlated.</summary>
        [Column(Order = 11)]
        [FieldGroup("Correlation back to the node's own records")]
        [References(typeof(HostedSession), Optional = true)]
        public string? IntentId { get; set; }

        /// <summary>The payer, when provider metadata named one.</summary>
        [Column(Order = 12)]
        [References(typeof(User), Optional = true)]
        public string? SubjectUserId { get; set; }

        /// <summary>Provider payment-session id, when the event names one.</summary>
        [Column(Order = 13)]
        [FieldGroup("Provider-side correlation (server-side only; never tenant-projected)")]
        public string? CheckoutSessionId { get; set; }

        /// <summary>Provider payment id, when the event names one.</summary>
        [Column(Order = 14)]
        public string? PaymentIntentId { get; set; }

        /// <summary>Provider charge id; the anchor a refund or dispute event arrives on.</summary>
        [Column(Order = 15)]
        public string? ChargeId { get; set; }

        /// <summary>SHA-256 of the exact bytes that were signature-verified. The body is not stored.</summary>
        [Column(Order = 16)]
        [FieldGroup("Evidence of exactly which bytes were accepted")]
        [Required(NotEmpty = true)]
        public string PayloadDigest { get; set; } = string.Empty;

        /// <summary>When the provider says the event happened.</summary>
        [Column(Order = 17)]
        [FieldGroup("Timestamps")]
        public DateTimeOffset? OccurredAt { get; set; }

        [Column(Order = 18)]
        [Default("time::now()")]
        [ReadOnly]
        public DateTimeOffset ReceivedAt { get; set; }

        /// <summary>When a handler completed the work. Absent while the row is not Applied.</summary>
        [Column(Order = 19)]
        public DateTimeOffset? AppliedAt { get; set; }

        [Column(Order = 20)]
        public string? ProviderAccountId { get; set; }

        [Column(Order = 21)]
        public string? ProviderObjectId { get; set; }
    }
}
