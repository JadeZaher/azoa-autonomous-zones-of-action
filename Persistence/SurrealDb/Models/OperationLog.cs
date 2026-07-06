// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the operation_log table.

#nullable enable

using System;
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
    [SurrealNote("B3 review: operation_log_idempotency_key UNIQUE on option<string>. NULL rows do NOT collide -- adapter MUST supply a non-NULL key on the claim path; api-safety-hardening §4 validator enforces this end-to-end.")]
    [Slice("bridge")]
    [Index("operation_log_idempotency_key", Fields = new[] { "idempotency_key" }, Unique = true)]
    [Index("operation_log_avatar_created", Fields = new[] { "avatar_id", "created_date" })]
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
            Minted,
            Burned,
            Exchanged,
            Swapped,
            Transferred,
            Deployed,
            Called,
        }

        [Id]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [References(typeof(Avatar), Optional = true)]
        public string? AvatarId { get; set; }

        [References(typeof(Wallet), Optional = true)]
        public string? WalletId { get; set; }

        [Required(NotEmpty = true)]
        public string OperationType { get; set; } = string.Empty;

        [FieldGroup("Status (OperationStatus closed set)")]
        [Inside("Pending", "Unknown", "Failed", "Completed", "AwaitingSignature",
                "Minted", "Burned", "Exchanged", "Swapped", "Transferred", "Deployed", "Called")]
        [Default("\"Pending\"")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public StatusKind Status { get; set; }

        // FLEXIBLE: the parameters bag holds arbitrary caller-supplied keys
        // (TokenUri, Amount, AssetType, WalletAddress, TxHash, ...). A bare
        // SCHEMAFULL option<object> rejects any undeclared sub-field with
        // "Found field 'parameters.X', but no such field exists" — FLEXIBLE lets
        // the object carry arbitrary keys. Mirrors Quest.Metadata.
        [Column(Flexible = true)]
        [FieldGroup("Opaque parameters bag (Dictionary<string,string>)")]
        public JsonElement? Parameters { get; set; }

        [FieldGroup("IMintOperation fields")]
        public string? TokenUri { get; set; }

        public long? Amount { get; set; }

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
        public string? IdempotencyKey { get; set; }

        [FieldGroup("Error detail (populated on failure)")]
        public string? Error { get; set; }

        [FieldGroup("tenant-consent-delegation AC4: acting tenant + required signing scope")]
        // The tenant that DROVE this value op via a child credential (null =
        // user-driven / platform-internal). Carried on the durable row so the signing
        // seam's live consent check survives the async saga-worker hop.
        [References(typeof(Avatar), Optional = true)]
        public string? ActingTenantId { get; set; }

        public string? SigningScope { get; set; }

        [FieldGroup("Timestamps")]
        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }

        public DateTimeOffset? CompletedDate { get; set; }

        [FieldGroup("Holon-asset link (economic-primitive-nodes)")]
        public string? AssetId { get; set; }

        public string? TxHash { get; set; }

        [References(typeof(Holon), Optional = true)]
        public string? HolonId { get; set; }
    }
}
