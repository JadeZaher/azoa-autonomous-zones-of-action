// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the fiat_payout table (W8.1 -- one row per cash-out).

#nullable enable

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("fiat_payout",
        Aggregate = "OffRampSaga (Services/Fiat/OffRamp/OffRampService.cs)",
        Guardrail = "G6 SCHEMAFULL; one row per cash-out request and it is the off-ramp's idempotency anchor")]
    [SurrealNote("One row is ONE cash-out: this many units left this person's account and became this much money in their own bank account. It is the mirror of on_ramp_lot and the only durable record of the outbound half of the loop -- the node keeps no balance ledger of its own (D47), so nothing on chain can say which payout a transfer belongs to.")]
    [SurrealNote("The record id is a deterministic Guid derived from subject|request key, so a double-tapped 'cash out' button settles into ONE row. It is a compact Guid rather than the SHA-256 hex on_ramp_lot uses because it is stamped into provider metadata as azoa_intent_id, and the webhook envelope only recovers a correlation that parses as a compact Guid -- that is how transfer.reversed finds its way back to this row without a second lookup table.")]
    [SurrealNote("status is the saga: Requested (claimed, nothing has moved), UnitsCollected (the units are back in the reserve and the money has NOT been sent -- the one state where the person is out of units and has no money), BankPending (the connected account is funded and the bank payout is pending), Paid (the bank payout is confirmed paid by the provider), Reversed (the provider pulled the money back out again), Returned (the cash-out was abandoned and the units were given back), Refused (refused before anything moved), NeedsOperator (only a person can move this on, and the reason is on the row).")]
    [SurrealNote("quantity_base_units is what the person gives up and amount_minor_units is what they are paid, both derived from the SAME registered price schedule the on-ramp sells at (tenant_price_schedule). Storing units_per_minor_unit and price_schedule_version on the row is what keeps the arithmetic re-derivable after that schedule is retired.")]
    [SurrealNote("consumed_lots is the FIFO receipt: which on_ramp_lot rows this cash-out drew down, and by how much. It exists so an abandoned cash-out can hand the provenance back rather than silently ageing units that were never actually cashed out. Units with NO lot -- units received from selling work or stake -- are unaged and appear here not at all (Q-X2).")]
    [SurrealNote("units_owed_base_units is the honest record of the worst case: the units were collected, the money never left, and giving the units back also failed. The row parks at NeedsOperator carrying the figure instead of the node quietly keeping units it was paid nothing for.")]
    [Slice("identity")]
    [Index("fiat_payout_request",
        Fields = new[] { "subject_user_id", "request_key", "environment", "chain_network" },
        Unique = true)]
    [Index("fiat_payout_account", Fields = new[] { "subject_user_id", "status" })]
    [Index("fiat_payout_transfer", Fields = new[] { "provider_transfer_id" })]
    [Index("fiat_payout_board", Fields = new[] { "status", "requested_at" })]
    public partial class FiatPayout : ISurrealRecord
    {
        public const string SchemaNameConst = "fiat_payout";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1)]
        [FieldGroup("Core identity (record id is a deterministic Guid over subject + request key)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        /// <summary>Whose units are leaving and whose bank account is paid. Never a body field.</summary>
        [Column(Order = 2)]
        [References(typeof(User))]
        public string SubjectUserId { get; set; } = string.Empty;

        /// <summary>The caller-stable key this cash-out was claimed under; a repeat resumes it.</summary>
        [Column(Order = 3)]
        [Required(NotEmpty = true)]
        public string RequestKey { get; set; } = string.Empty;

        /// <summary>The tenant that drove the cash-out on the person's behalf, when one did.</summary>
        [Column(Order = 4)]
        [References(typeof(User), Optional = true)]
        public string? TenantId { get; set; }

        [Column(Order = 5)]
        [FieldGroup("What leaves the account, and what is paid for it")]
        [Required(NotEmpty = true)]
        public string ChainType { get; set; } = string.Empty;

        [Column(Order = 6)]
        [Required(NotEmpty = true)]
        public string AssetId { get; set; } = string.Empty;

        /// <summary>Units the person gives up, in base units, as an exact decimal string.</summary>
        [Column(Order = 7)]
        [Default("\"0\"")]
        public string QuantityBaseUnits { get; set; } = "0";

        /// <summary>The money the person is paid, in minor units. Derived, never client-supplied.</summary>
        [Column(Order = 8)]
        [Default("0")]
        public long AmountMinorUnits { get; set; }

        [Column(Order = 9)]
        [Required(NotEmpty = true)]
        public string CurrencyCode { get; set; } = string.Empty;

        /// <summary>The exact price-schedule row that converted units into money.</summary>
        [Column(Order = 10)]
        [FieldGroup("The registered price that converted the units (the same schedule the on-ramp sells at)")]
        [References(typeof(TenantPriceSchedule), Optional = true)]
        public string? PriceScheduleId { get; set; }

        [Column(Order = 11)]
        [Default("0")]
        public long PriceScheduleVersion { get; set; }

        /// <summary>The rate applied, copied onto the row so it survives a retired schedule.</summary>
        [Column(Order = 12)]
        [Default("\"0\"")]
        public string UnitsPerMinorUnit { get; set; } = "0";

        /// <summary>Which on-ramp lots this cash-out drew down, keyed by lot id, in base units.</summary>
        [Column(Order = 13, Flexible = true)]
        [FieldGroup("Source-aged provenance: the on-ramp lots this cash-out consumed, FIFO")]
        public JsonElement? ConsumedLots { get; set; }

        /// <summary>Units drawn from lots past their reversal window; the rest were unaged.</summary>
        [Column(Order = 14)]
        [Default("\"0\"")]
        public string AgedQuantityBaseUnits { get; set; } = "0";

        /// <summary>The person's connected payout account at the provider.</summary>
        [Column(Order = 15)]
        [FieldGroup("Provider-side provenance the reversal pipeline anchors on")]
        public string? ConnectedAccountId { get; set; }

        /// <summary>The provider transfer that paid the money out.</summary>
        [Column(Order = 16)]
        public string? ProviderTransferId { get; set; }

        /// <summary>The rail reference for the units moving out of the person's account.</summary>
        [Column(Order = 17)]
        public string? UnitsCollectedTransactionId { get; set; }

        /// <summary>The rail reference for units handed back when a cash-out was abandoned.</summary>
        [Column(Order = 18)]
        public string? UnitsReturnedTransactionId { get; set; }

        [Column(Order = 19)]
        [FieldGroup("Lifecycle")]
        [Inside("Requested", "UnitsCollected", "BankPending", "Paid", "Reversed", "Returned", "Refused", "NeedsOperator")]
        [Default("\"Requested\"")]
        public string Status { get; set; } = "Requested";

        /// <summary>Why the cash-out is where it is. Always present on Refused and NeedsOperator.</summary>
        [Column(Order = 20)]
        public string? StatusReason { get; set; }

        /// <summary>Units the node took and could neither pay for nor give back. The node's own debt.</summary>
        [Column(Order = 21)]
        [Default("\"0\"")]
        public string UnitsOwedBaseUnits { get; set; } = "0";

        /// <summary>Money the provider pulled back out after paying it. Re-read server-side.</summary>
        [Column(Order = 22)]
        [Default("0")]
        public long ReversedMinorUnits { get; set; }

        /// <summary>The reversal ledger row this payout's reversal was recorded as (W4.3).</summary>
        [Column(Order = 23)]
        [References(typeof(FiatReversal), Optional = true)]
        public string? ReversalId { get; set; }

        /// <summary>Saga attempts consumed. The bound on how long the node retries a stuck cash-out.</summary>
        [Column(Order = 24)]
        [Default("0")]
        public long AttemptCount { get; set; }

        [Column(Order = 25)]
        public DateTimeOffset RequestedAt { get; set; }

        [Column(Order = 26)]
        public DateTimeOffset? UnitsCollectedAt { get; set; }

        [Column(Order = 27)]
        public DateTimeOffset? PaidAt { get; set; }

        [Column(Order = 28)]
        public DateTimeOffset? ReversedAt { get; set; }

        [Column(Order = 29)]
        [FieldGroup("Optimistic concurrency + timestamps")]
        [Default("0")]
        public long Version { get; set; }

        [Column(Order = 30)]
        [Default("time::now()")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }

        [Column(Order = 31)]
        public DateTimeOffset UpdatedAt { get; set; }

        /// <summary>The connected-account bank payout, distinct from the funding transfer.</summary>
        [Column(Order = 32)]
        public string? ProviderPayoutId { get; set; }

        /// <summary>Start of the current provider idempotency window.</summary>
        [Column(Order = 33)]
        public DateTimeOffset? BankPayoutStartedAt { get; set; }

        /// <summary>Advances only after a definitive insufficient-funds refusal.</summary>
        [Column(Order = 34)]
        [Default("0")]
        public long BankPayoutAttempt { get; set; }

        [Column(Order = 35)]
        [Default("\"unbound\"")]
        public string Environment { get; set; } = "unbound";

        [Column(Order = 36)]
        [Default("\"unbound\"")]
        public string ChainNetwork { get; set; } = "unbound";

        [Column(Order = 37)]
        [Default("false")]
        public bool RecoveryRequested { get; set; }

        [Column(Order = 38)]
        [Default("0")]
        public long RecoveryReversalAmount { get; set; }

        [Column(Order = 39)]
        public DateTimeOffset? RecoveryStartedAt { get; set; }

        [Column(Order = 40)]
        public string? ProviderTransferReversalId { get; set; }

        [Column(Order = 41)]
        [Default("\"0\"")]
        public string RestoredQuantityBaseUnits { get; set; } = "0";

        [Column(Order = 42)]
        public string? RestitutionKey { get; set; }

        [Column(Order = 43)]
        [Default("\"0\"")]
        public string RestitutionTargetBaseUnits { get; set; } = "0";

        [Column(Order = 44)]
        [Default("\"[]\"")]
        public string RecoveryAuditJson { get; set; } = "[]";

        [Column(Order = 45)]
        public string? ReturnPendingTransactionId { get; set; }

        [Column(Order = 46)]
        public string? ReturnPendingIdempotencyKey { get; set; }

        [Column(Order = 47)]
        [Default("0")]
        public long RecoveryReversalAttempt { get; set; }

        /// <summary>
        /// Deterministic record id for the one cash-out a request key may settle into: a compact
        /// Guid over the first 16 bytes of SHA-256(subject|key).
        /// </summary>
        /// <remarks>
        /// It has to PARSE as a compact Guid. The provider transfer carries this id in its
        /// metadata, and <c>StripeEventEnvelope</c> only recovers a correlation that parses that
        /// way â€” so a hex digest here would leave <c>transfer.reversed</c> with no way home.
        /// </remarks>
        public static string RecordIdFor(Guid subjectUserId, string requestKey, string environment = "test", string chainNetwork = "Devnet")
        {
            var canonical = $"fiat-payout|{environment}|{chainNetwork}|{subjectUserId:N}|{requestKey.Trim()}";
            var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
            return new Guid(digest.AsSpan(0, 16)).ToString("N");
        }

        /// <summary>The lots this cash-out consumed, as lot id to base units. Empty when none.</summary>
        public IReadOnlyDictionary<string, string> ReadConsumedLots()
        {
            if (ConsumedLots is not { ValueKind: JsonValueKind.Object } element)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var consumed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    consumed[property.Name] = property.Value.GetString() ?? "0";
            }

            return consumed;
        }

        /// <summary>Replaces the consumed-lot receipt. An empty map clears the column.</summary>
        public void WriteConsumedLots(IReadOnlyDictionary<string, string> consumed)
        {
            ArgumentNullException.ThrowIfNull(consumed);

            if (consumed.Count == 0)
            {
                ConsumedLots = null;
                return;
            }

            ConsumedLots = JsonSerializer.SerializeToElement(consumed);
        }
    }
}
