// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the fiat_reversal table (W4.3 -- one row per reversal of a card payment).

#nullable enable

using System.Security.Cryptography;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("fiat_reversal",
        Aggregate = "FiatReversalSaga (Services/Fiat/Reversal/FiatReversalEventHandler.cs)",
        Guardrail = "G6 SCHEMAFULL; one row per reversed payment and it is the compensation saga's anchor")]
    [SurrealNote("One row is ONE reversal of one card payment: a refund the node issued, or a dispute the payer raised. The four provider events the pipeline handles -- charge.refunded, charge.dispute.created, charge.dispute.funds_withdrawn and charge.dispute.funds_reinstated -- each land as their own fiat_event inbox row and then MOVE this row. That split is deliberate: fiat_event is the per-delivery evidence, this table is the standing fact of what the node owes back and what it managed to recover.")]
    [SurrealNote("The record id is the SHA-256 of provider|scope|provider_object_id, where scope is 'refund' or 'dispute'. A charge that is both partially refunded AND disputed is therefore two reversals, while every redelivery of one dispute's lifecycle addresses one row. A second partial refund of the same charge re-reads the provider's CUMULATIVE amount_refunded onto the same row, so partial refunds accumulate instead of racing.")]
    [SurrealNote("amount_minor_units is the money taken back, re-read from the provider server-side. It is NEVER parsed from the webhook body: a body-supplied reversal amount would be the money-printer of the on-ramp read backwards -- claw back more units than the payment ever bought. source_amount_minor_units is the original payment, and the two together are what make a PARTIAL reversal an exact proportion rather than an estimate.")]
    [SurrealNote("funds_withdrawn separates 'a dispute exists' from 'the money is gone'. charge.dispute.created opens the row and (per the flow's risk policy) flags the account, but recovers nothing: clawing units back over a dispute that may still be resolved in the node's favour punishes a person for asking a question. charge.dispute.funds_withdrawn is what actually starts the compensation saga.")]
    [SurrealNote("recovery_path records WHICH rung of the compensation ladder answered: Balance (the units were still in the payer's account), Hold (an open hold their money had funded was refunded), Purchase (the units they had bought were clawed back), Reserve (nothing was recoverable and the platform absorbed the loss), Mixed (more than one rung contributed). None means no rung has run yet. Absorbed loss is recorded, never hidden -- absorbed_quantity_base_units is the node's own bill.")]
    [SurrealNote("account_flagged is a CONTROL, not a note: RiskHoldValueGate (Services/Fiat/Reversal/RiskHoldValueGate.cs) refuses the D46 value gate for any account carrying an unresolved flag, so a flagged payer cannot buy more units while the node is still out of pocket. Clearing it is an operator action and is stamped with who cleared it.")]
    [SurrealNote("reinstatement_owed_base_units is the honest record of the awkward case: the node clawed units back, then the provider reinstated the disputed funds. The node now owes those units again, and re-issuing them is the on-ramp's job rather than this pipeline's, so the row parks at NeedsOperator with the figure on it instead of silently keeping the units.")]
    [Slice("identity")]
    [Index("fiat_reversal_object",
        Fields = new[] { "provider", "provider_object_id" },
        Unique = true)]
    [Index("fiat_reversal_charge", Fields = new[] { "charge_id" })]
    [Index("fiat_reversal_open", Fields = new[] { "status", "opened_at" })]
    [Index("fiat_reversal_lot", Fields = new[] { "lot_id" })]
    [Index("fiat_reversal_flag", Fields = new[] { "subject_user_id", "account_flagged" })]
    public partial class FiatReversal : ISurrealRecord
    {
        public const string SchemaNameConst = "fiat_reversal";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1)]
        [FieldGroup("Core identity (record id is the SHA-256 of provider|scope|provider object id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        /// <summary>Payment provider that reported the reversal.</summary>
        [Column(Order = 2)]
        [Required(NotEmpty = true)]
        public string Provider { get; set; } = string.Empty;

        /// <summary>The provider object this row is about: the charge for a refund, the dispute for a dispute.</summary>
        [Column(Order = 3)]
        [Required(NotEmpty = true)]
        public string ProviderObjectId { get; set; } = string.Empty;

        /// <summary>Which kind of reversal this is; see the table notes.</summary>
        [Column(Order = 4)]
        [Inside("Refund", "Dispute")]
        [Default("\"Refund\"")]
        public string Kind { get; set; } = "Refund";

        /// <summary>The exact provider event type that last moved this row.</summary>
        [Column(Order = 5)]
        [Required(NotEmpty = true)]
        public string LastEventType { get; set; } = string.Empty;

        /// <summary>The provider event id of that delivery; the fiat_event row carrying the evidence.</summary>
        [Column(Order = 6)]
        [Required(NotEmpty = true)]
        public string ProviderEventId { get; set; } = string.Empty;

        /// <summary>The charge the money was taken back from. The anchor every reversal event names.</summary>
        [Column(Order = 7)]
        [FieldGroup("Correlation back to the payment this unwinds")]
        [Required(NotEmpty = true)]
        public string ChargeId { get; set; } = string.Empty;

        [Column(Order = 8)]
        public string? PaymentIntentId { get; set; }

        [Column(Order = 9)]
        public string? CheckoutSessionId { get; set; }

        /// <summary>The funding intent the payment settled, when it could be correlated.</summary>
        [Column(Order = 10)]
        [References(typeof(HostedSession), Optional = true)]
        public string? IntentId { get; set; }

        /// <summary>The issuance this reversal unwinds. Absent means nothing was ever credited.</summary>
        [Column(Order = 11)]
        [References(typeof(OnRampLot), Optional = true)]
        public string? LotId { get; set; }

        /// <summary>Whose units are being unwound, and whose account the flag lands on.</summary>
        [Column(Order = 12)]
        [References(typeof(User), Optional = true)]
        public string? SubjectUserId { get; set; }

        /// <summary>The tenant that drove the original payment, when one did.</summary>
        [Column(Order = 13)]
        [References(typeof(User), Optional = true)]
        public string? TenantId { get; set; }

        /// <summary>The flow the payment funded; the key the per-flow risk policy is selected by.</summary>
        [Column(Order = 14)]
        public string? FlowPurpose { get; set; }

        /// <summary>The money taken back, in minor units. Re-read from the provider, never from a body.</summary>
        [Column(Order = 15)]
        [FieldGroup("The money taken back, re-read from the provider (never the webhook body)")]
        [Default("0")]
        public long AmountMinorUnits { get; set; }

        [Column(Order = 16)]
        [Required(NotEmpty = true)]
        public string CurrencyCode { get; set; } = string.Empty;

        /// <summary>What the original payment was, so a partial reversal is an exact proportion.</summary>
        [Column(Order = 17)]
        [Default("0")]
        public long SourceAmountMinorUnits { get; set; }

        /// <summary>True when less than the whole payment came back.</summary>
        [Column(Order = 18)]
        [Default("false")]
        public bool IsPartial { get; set; }

        /// <summary>Whether the provider has actually taken the money yet; see the table notes.</summary>
        [Column(Order = 19)]
        [Default("false")]
        public bool FundsWithdrawn { get; set; }

        [Column(Order = 20)]
        [FieldGroup("What has to be unwound, and what actually was")]
        public string? ChainType { get; set; }

        [Column(Order = 21)]
        public string? AssetId { get; set; }

        /// <summary>Units this reversal must recover, in base units, as an exact decimal string.</summary>
        [Column(Order = 22)]
        [Default("\"0\"")]
        public string TargetQuantityBaseUnits { get; set; } = "0";

        /// <summary>Units actually recovered across every rung of the ladder.</summary>
        [Column(Order = 23)]
        [Default("\"0\"")]
        public string RecoveredQuantityBaseUnits { get; set; } = "0";

        /// <summary>Units nothing could recover, which the platform is therefore out of pocket.</summary>
        [Column(Order = 24)]
        [Default("\"0\"")]
        public string AbsorbedQuantityBaseUnits { get; set; } = "0";

        /// <summary>Units the node clawed back and then owed again after a reinstatement.</summary>
        [Column(Order = 25)]
        [Default("\"0\"")]
        public string ReinstatementOwedBaseUnits { get; set; } = "0";

        /// <summary>Which rung of the ladder answered; see the table notes.</summary>
        [Column(Order = 26)]
        [Inside("None", "Balance", "Hold", "Purchase", "Reserve", "Mixed")]
        [Default("\"None\"")]
        public string RecoveryPath { get; set; } = "None";

        /// <summary>The rail's reference for the recovery move, when one was broadcast.</summary>
        [Column(Order = 27)]
        public string? RecoveryTransactionId { get; set; }

        /// <summary>The durable operation that moved the units back, when one was recorded.</summary>
        [Column(Order = 28)]
        [References(typeof(OperationLog), Optional = true)]
        public string? OperationId { get; set; }

        /// <summary>Where the reversal stands; see the table notes.</summary>
        [Column(Order = 29)]
        [FieldGroup("Lifecycle")]
        [Inside("Open", "Recovering", "Recovered", "PartiallyRecovered", "Absorbed", "Reinstated", "Unmatched", "NeedsOperator")]
        [Default("\"Open\"")]
        public string Status { get; set; } = "Open";

        /// <summary>Why the reversal is where it is. Always present once a rung has refused.</summary>
        [Column(Order = 30)]
        public string? StatusReason { get; set; }

        /// <summary>Compensation attempts consumed.</summary>
        [Column(Order = 31)]
        [Default("0")]
        public long AttemptCount { get; set; }

        [Column(Order = 32)]
        public DateTimeOffset OpenedAt { get; set; }

        [Column(Order = 33)]
        public DateTimeOffset? ResolvedAt { get; set; }

        [Column(Order = 34)]
        public DateTimeOffset? ReinstatedAt { get; set; }

        /// <summary>Whether this account is barred from further value actions. A control, not a note.</summary>
        [Column(Order = 35)]
        [FieldGroup("Risk flag on the account (the value gate is the read side)")]
        [Default("false")]
        public bool AccountFlagged { get; set; }

        [Column(Order = 36)]
        public string? FlagReason { get; set; }

        [Column(Order = 37)]
        public DateTimeOffset? FlagClearedAt { get; set; }

        /// <summary>The operator who cleared the flag. Absent while the flag stands.</summary>
        [Column(Order = 38)]
        [References(typeof(User), Optional = true)]
        public string? FlagClearedBy { get; set; }

        [Column(Order = 39)]
        [FieldGroup("Optimistic concurrency + timestamps")]
        [Default("0")]
        public long Version { get; set; }

        [Column(Order = 40)]
        [Default("time::now()")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }

        [Column(Order = 41)]
        public DateTimeOffset UpdatedAt { get; set; }

        /// <summary>Purchase-recovery units this row has counted; reconciles against the immutable recovery receipts.</summary>
        [Column(Order = 42)]
        [Default("ALWAYS \"0\"")]
        public string PurchaseRecoveredBaseUnits { get; set; } = "0";

        /// <summary>Claim key of the recovery move whose outcome parked this reversal; cleared by the operator resolution.</summary>
        [Column(Order = 43)]
        public string? PendingMoveKey { get; set; }

        /// <summary>Scope word for a refund reversal; part of the record id.</summary>
        public const string RefundScope = "refund";

        /// <summary>Scope word for a dispute reversal; part of the record id.</summary>
        public const string DisputeScope = "dispute";

        /// <summary>
        /// Deterministic record id for one reversal. A charge that is both partially refunded and
        /// disputed yields two ids because the scope differs; every redelivery of one dispute's
        /// lifecycle yields the same id.
        /// </summary>
        public static string RecordIdFor(string provider, string scope, string providerObjectId)
        {
            var canonical =
                $"fiat-reversal|{provider.Trim().ToLowerInvariant()}"
                + $"|{scope.Trim().ToLowerInvariant()}|{providerObjectId.Trim()}";
            return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }
    }
}
