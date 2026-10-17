// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the operation_log table.

#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("operation_log",
        Aggregate = "BlockchainOperation (Models/BlockchainOperation.cs)",
        Guardrail = "G6 SCHEMAFULL, G2 idempotency unique index")]
    [SurrealNote("Status values from OperationStatus constants; Parameters is Dictionary<string,string> stored as object.")]
    [SurrealNote("D-3: NeedsOperator parks a client-signed transfer the chain cannot settle on its own (an unexpired Ethereum signature); see Services/Blockchain/AGENTS.md.")]
    [SurrealNote("B3 review: operation_log_idempotency_key UNIQUE on option<string>. NONE rows do NOT collide (verified on SurrealDB 3.1.4) -- adapter MUST supply a non-NONE key on the claim path; api-safety-hardening §4 validator enforces this end-to-end.")]
    [SurrealNote("D13: amount is a STRING of decimal base units, not a number. The domain type is RailAmount (BigInteger base units), so a numeric column would re-impose the 64-bit ceiling the widening removed -- an ordinary wei-denominated Ethereum amount does not fit in i64. Written via RailAmount.ToBaseUnitString(), read via RailAmount.TryParse.")]
    [SurrealNote("W3.3: an allocation's caller-supplied Purpose, and an issuance's ReserveAddress / ReservePartition / AllocationKind, live as keys INSIDE the FLEXIBLE parameters object -- deliberately, not as columns. SurrealBlockchainOperationStore.ToRow is the only writer of this table and it maps the whole parameters bag; it does not map arbitrary new columns, so a dedicated purpose column would be schema that nothing ever fills. Add one only together with the ToRow mapping.")]
    [SurrealNote("D16: value_currency_code / value_minor_units / value_rate / value_rate_as_of / value_rate_source are the fiat value stamped at settlement. They move as a group: all five NONE means the operation was never priced, which is deliberately distinguishable from value_rate = \"0\" (a real quote of zero). A partial stamp is not a stamp and reads back as unpriced. No rate source exists in this codebase yet -- IValueRateSource is the seam, and until something implements it every row is legitimately unstamped. Never backfill these from a later price: the point of storing the rate and its timestamp is that the figure stays auditable and re-derivable from what was actually known at settlement.")]
    [Slice("bridge")]
    [Index("operation_log_idempotency_key", Fields = new[] { "idempotency_key" }, Unique = true)]
    [Index("operation_log_user_created", Fields = new[] { "user_id", "created_date" })]
    [Index("operation_log_status", Fields = new[] { "status" })]
    public partial class OperationLog : ISurrealRecord
    {
        public const string SchemaNameConst = "operation_log";
        public string SchemaName => SchemaNameConst;

        public enum StatusKind
        {
            Pending,
            Unknown,
            Failed,
            Completed,
            AwaitingSignature,
            PendingConfirmation,
            Minted,
            Burned,
            Exchanged,
            Swapped,
            Transferred,
            Deployed,
            Called,
            NeedsOperator,
        }

        [Id]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [References(typeof(User), Optional = true)]
        public string? UserId { get; set; }

        [References(typeof(Wallet), Optional = true)]
        public string? WalletId { get; set; }

        [Required(NotEmpty = true)]
        public string OperationType { get; set; } = string.Empty;

        [FieldGroup("Status (OperationStatus closed set)")]
        [Inside("Pending", "Unknown", "Failed", "Completed", "AwaitingSignature", "PendingConfirmation",
                "Minted", "Burned", "Exchanged", "Swapped", "Transferred", "Deployed", "Called", "NeedsOperator")]
        [Default("\"Pending\"")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public StatusKind Status { get; set; }

        // FLEXIBLE: the parameters bag holds arbitrary caller-supplied keys
        // (TokenUri, Amount, AssetType, WalletAddress, TxHash, ...). A bare
        // SCHEMAFULL option<object> rejects any undeclared sub-field with
        // "Found field 'parameters.X', but no such field exists" — FLEXIBLE lets
        // the object carry arbitrary keys. Mirrors ActionDefinition.Metadata.
        [Column(Flexible = true)]
        [FieldGroup("Opaque parameters bag (Dictionary<string,string>)")]
        public JsonElement? Parameters { get; set; }

        [FieldGroup("IMintOperation fields")]
        public string? TokenUri { get; set; }

        // D13: decimal base units as a string -- see the table note.
        [Assert("$value = NONE OR string::matches($value, \"^[0-9]+$\")")]
        public string? Amount { get; set; }

        public string? AssetType { get; set; }

        [FieldGroup("IExchangeOperation fields")]
        [References(typeof(Holon), Optional = true)]
        public string? SourceHolonId { get; set; }

        [References(typeof(Holon), Optional = true)]
        public string? TargetHolonId { get; set; }

        public string? ExchangeRate { get; set; }

        [FieldGroup("ITransferOperation fields")]
        public string? RecipientAddress { get; set; }

        [FieldGroup("G2 idempotency")]
        [Assert("$value = NONE OR string::matches($value, \"^[0-9a-f]{64}$\")")]
        public string? IdempotencyKey { get; set; }

        [FieldGroup("Allocation receipt initiator context")]
        [References(typeof(User), Optional = true)]
        public string? InitiatorUserId { get; set; }

        [References(typeof(ApiKey), Optional = true)]
        public string? InitiatorApiKeyId { get; set; }

        [FieldGroup("Error detail (populated on failure)")]
        public string? Error { get; set; }

        [FieldGroup("tenant-consent-delegation AC4: acting tenant + required signing scope")]
        // The tenant that DROVE this value op via a child credential (null =
        // user-driven / platform-internal). Carried on the durable row so the signing
        // seam's live consent check survives the async saga-worker hop.
        [References(typeof(User), Optional = true)]
        public string? ActingTenantId { get; set; }

        public string? SigningScope { get; set; }

        [FieldGroup("Timestamps")]
        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }

        public DateTimeOffset? CompletedDate { get; set; }

        [FieldGroup("D16 fiat value at transaction time (all-NONE = never priced; see the table note)")]
        // ISO-4217 code. Carried as data so a second currency is a value change, not a schema break (D15).
        [Assert("$value = NONE OR string::matches($value, \"^[A-Z]{3}$\")")]
        public string? ValueCurrencyCode { get; set; }

        // The stamped value in the currency's minor unit (USD cents), decimal string.
        [Assert("$value = NONE OR string::matches($value, \"^[0-9]+$\")")]
        public string? ValueMinorUnits { get; set; }

        // Minor units per one base unit, as an invariant decimal string. "0" is a real
        // quoted rate; NONE is "no rate source could price this".
        [Assert("$value = NONE OR string::matches($value, \"^[0-9]+(\\\\.[0-9]+)?$\")")]
        public string? ValueRate { get; set; }

        // When the applied rate was OBSERVED, which is not necessarily when it was applied.
        public DateTimeOffset? ValueRateAsOf { get; set; }

        public string? ValueRateSource { get; set; }

        [FieldGroup("Holon-asset link (economic-primitive-nodes)")]
        public string? AssetId { get; set; }

        public string? TxHash { get; set; }

        [References(typeof(Holon), Optional = true)]
        public string? HolonId { get; set; }
    }
}
