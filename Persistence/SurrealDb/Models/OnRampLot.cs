// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the on_ramp_lot table (W4.2 -- one lot per fiat-funded issuance).

#nullable enable

using System;
using System.Security.Cryptography;
using System.Text;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("on_ramp_lot",
        Aggregate = "OnRampSaga (Services/Fiat/OnRamp/OnRampSettlementHandler.cs)",
        Guardrail = "G6 SCHEMAFULL; one row per funding intent and it is the on-ramp's idempotency anchor")]
    [SurrealNote("One row is ONE fiat-funded issuance: this much money became this many units for this person at this price, out of this card payment. The units themselves are a FUNGIBLE asset on chain, so the chain cannot say which of a person's units were bought with a card and which were earned -- and the node keeps no balance ledger of its own (D47). This table is the only per-lot provenance there is, which is what the source-aged cash-out rule (W8.1) consumes FIFO. Units received from selling work or stake have NO lot and are therefore unaged.")]
    [SurrealNote("The record id is the SHA-256 of the funding intent id, so one intent can settle into at most one lot no matter how many times the provider redelivers. checkout.session.completed and a later async_payment_succeeded for the same session address the same row.")]
    [SurrealNote("status is the saga: Pending (money cleared, units not yet confirmed), Issued (units are in the person's account), Refused (the node decided not to credit -- the money is owed back and the reversal pipeline owes a refund), Stuck (the node tried attempt_count times and could not determine the outcome; reconcile before refunding or crediting), Reversed (W4.3 clawed it back). Refused and Stuck exist so the provider can be told to stop retrying WITHOUT the charge becoming invisible.")]
    [SurrealNote("fiat_minor_units and currency_code are the real money the provider confirmed, re-read server-side. Together with price_schedule_version and units_per_minor_unit they make the quantity re-derivable: this is the D16 value of the issuance stated honestly, because on the on-ramp the fiat amount is actually known rather than modelled.")]
    [SurrealNote("quantity_base_units is what the person RECEIVED (net of the node fee); gross_quantity_base_units is what the price schedule produced and node_fee_base_units is the difference. W8.1 ages and consumes the net figure -- crediting the gross would let a cash-out draw units that were never issued.")]
    [SurrealNote("consumed_base_units is W8.1's FIFO watermark. It lives here rather than in a separate table so a partially cashed-out lot cannot disagree with itself; version is the compare-and-swap anchor every writer of this row uses.")]
    [SurrealNote("reversal_window_ends is issued_at plus the RAIL's declared chargeback window (Core/SettlementProfile.cs), not a constant chosen here. Until it passes these units are spendable inside the platform but are not cashable out, because the money that bought them can still be taken back.")]
    [Slice("identity")]
    [Index("on_ramp_lot_intent", Fields = new[] { "intent_id" }, Unique = true)]
    [Index("on_ramp_lot_fifo", Fields = new[] { "subject_user_id", "status", "issued_at" })]
    [Index("on_ramp_lot_payment", Fields = new[] { "source_payment_intent_id" })]
    [Index("on_ramp_lot_charge", Fields = new[] { "source_charge_id" })]
    public partial class OnRampLot : ISurrealRecord
    {
        public const string SchemaNameConst = "on_ramp_lot";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1)]
        [FieldGroup("Core identity (record id is the SHA-256 of the funding intent this lot settles)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        /// <summary>The funding intent this issuance settles; one intent, at most one lot.</summary>
        [Column(Order = 2)]
        [References(typeof(HostedSession))]
        public string IntentId { get; set; } = string.Empty;

        /// <summary>Whose account the units went into. Read from the intent, never from webhook metadata.</summary>
        [Column(Order = 3)]
        [References(typeof(User))]
        public string SubjectUserId { get; set; } = string.Empty;

        /// <summary>The tenant that drove the payment, when one did.</summary>
        [Column(Order = 4)]
        [References(typeof(User), Optional = true)]
        public string? TenantId { get; set; }

        [Column(Order = 5)]
        [FieldGroup("What was issued, and out of what money")]
        [Required(NotEmpty = true)]
        public string ChainType { get; set; } = string.Empty;

        [Column(Order = 6)]
        [Required(NotEmpty = true)]
        public string AssetId { get; set; } = string.Empty;

        /// <summary>Units the person actually received, in base units, as an exact decimal string.</summary>
        [Column(Order = 7)]
        [Default("\"0\"")]
        public string QuantityBaseUnits { get; set; } = "0";

        /// <summary>What the price schedule produced before the node fee.</summary>
        [Column(Order = 8)]
        [Default("\"0\"")]
        public string GrossQuantityBaseUnits { get; set; } = "0";

        /// <summary>The node fee retained from this issuance, in base units.</summary>
        [Column(Order = 9)]
        [Default("\"0\"")]
        public string NodeFeeBaseUnits { get; set; } = "0";

        /// <summary>How much of this lot a cash-out has already consumed (W8.1 FIFO watermark).</summary>
        [Column(Order = 10)]
        [Default("\"0\"")]
        public string ConsumedBaseUnits { get; set; } = "0";

        /// <summary>The money the provider confirmed, in minor units. Never a figure a client sent.</summary>
        [Column(Order = 11)]
        [Default("0")]
        public long FiatMinorUnits { get; set; }

        [Column(Order = 12)]
        [Required(NotEmpty = true)]
        public string CurrencyCode { get; set; } = string.Empty;

        /// <summary>The exact price-schedule row that converted the money.</summary>
        [Column(Order = 13)]
        [FieldGroup("The registered price that converted the money (never a client-supplied quantity)")]
        [References(typeof(TenantPriceSchedule), Optional = true)]
        public string? PriceScheduleId { get; set; }

        [Column(Order = 14)]
        [Default("0")]
        public long PriceScheduleVersion { get; set; }

        /// <summary>The rate applied, copied onto the lot so it survives a retired schedule.</summary>
        [Column(Order = 15)]
        [Default("\"0\"")]
        public string UnitsPerMinorUnit { get; set; } = "0";

        [Column(Order = 16)]
        [FieldGroup("Provider-side provenance the reversal pipeline anchors on")]
        public string? SourceCheckoutSessionId { get; set; }

        [Column(Order = 17)]
        public string? SourcePaymentIntentId { get; set; }

        /// <summary>The charge a refund or dispute arrives against; the source this lot is aged from.</summary>
        [Column(Order = 18)]
        public string? SourceChargeId { get; set; }

        [Column(Order = 19)]
        [FieldGroup("Lifecycle")]
        [Inside("Pending", "Issued", "Refused", "Stuck", "Reversed")]
        [Default("\"Pending\"")]
        public string Status { get; set; } = "Pending";

        /// <summary>Why the lot is where it is. Always present on Refused and Stuck.</summary>
        [Column(Order = 20)]
        public string? StatusReason { get; set; }

        /// <summary>The durable operation that moved the units out of the reserve.</summary>
        [Column(Order = 21)]
        [References(typeof(OperationLog), Optional = true)]
        public string? OperationId { get; set; }

        /// <summary>Issuance attempts consumed. The bound on how long the provider may retry.</summary>
        [Column(Order = 22)]
        [Default("0")]
        public long AttemptCount { get; set; }

        [Column(Order = 23)]
        public DateTimeOffset? IssuedAt { get; set; }

        /// <summary>issued_at plus the rail's chargeback window; before it these units are not cashable.</summary>
        [Column(Order = 24)]
        public DateTimeOffset? ReversalWindowEnds { get; set; }

        [Column(Order = 25)]
        [FieldGroup("Optimistic concurrency + timestamps")]
        [Default("0")]
        public long Version { get; set; }

        [Column(Order = 26)]
        [Default("time::now()")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }

        [Column(Order = 27)]
        public DateTimeOffset UpdatedAt { get; set; }

        /// <summary>Deterministic record id for the one lot a funding intent may settle into.</summary>
        public static string RecordIdFor(string intentId)
        {
            var canonical = $"on-ramp|{intentId.Trim().ToLowerInvariant()}";
            return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }
    }
}
