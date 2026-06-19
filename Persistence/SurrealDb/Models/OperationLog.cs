// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the operation_log table.

#nullable enable

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
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

        [Id, Column(Order = 1, Type = "string")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [References(typeof(Avatar), Optional = true)]
        [JsonPropertyName("avatar_id")]
        public string? AvatarId { get; set; }

        [Column(Order = 3)]
        [References(typeof(Wallet), Optional = true)]
        [JsonPropertyName("wallet_id")]
        public string? WalletId { get; set; }

        [Column(Order = 4, Type = "string")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("operation_type")]
        public string OperationType { get; set; } = string.Empty;

        [Column(Order = 5, Type = "string")]
        [FieldGroup("Status (OperationStatus closed set)")]
        [Inside("Pending", "Unknown", "Failed", "Completed", "AwaitingSignature",
                "Minted", "Burned", "Exchanged", "Swapped", "Transferred", "Deployed", "Called")]
        [Default("\"Pending\"")]
        [JsonPropertyName("status"), JsonConverter(typeof(JsonStringEnumConverter))]
        public StatusKind Status { get; set; }

        [Column(Order = 6, Type = "option<object>")]
        [FieldGroup("Opaque parameters bag (Dictionary<string,string>)")]
        [JsonPropertyName("parameters")]
        public JsonElement? Parameters { get; set; }

        [Column(Order = 7, Type = "option<string>")]
        [FieldGroup("IMintOperation fields")]
        [JsonPropertyName("token_uri")]
        public string? TokenUri { get; set; }

        [Column(Order = 8, Type = "option<int>")]
        [JsonPropertyName("amount")]
        public long? Amount { get; set; }

        [Column(Order = 9, Type = "option<string>")]
        [JsonPropertyName("asset_type")]
        public string? AssetType { get; set; }

        [Column(Order = 10)]
        [FieldGroup("IExchangeOperation fields")]
        [References(typeof(Holon), Optional = true)]
        [JsonPropertyName("source_holon_id")]
        public string? SourceHolonId { get; set; }

        [Column(Order = 11)]
        [References(typeof(Holon), Optional = true)]
        [JsonPropertyName("target_holon_id")]
        public string? TargetHolonId { get; set; }

        [Column(Order = 12, Type = "option<string>")]
        [JsonPropertyName("exchange_rate")]
        public string? ExchangeRate { get; set; }

        [Column(Order = 13, Type = "option<string>")]
        [FieldGroup("ITransferOperation fields")]
        [JsonPropertyName("recipient_address")]
        public string? RecipientAddress { get; set; }

        [Column(Order = 14, Type = "option<string>")]
        [FieldGroup("G2 idempotency")]
        [JsonPropertyName("idempotency_key")]
        public string? IdempotencyKey { get; set; }

        [Column(Order = 15, Type = "option<string>")]
        [FieldGroup("Error detail (populated on failure)")]
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [Column(Order = 16, Type = "datetime")]
        [FieldGroup("Timestamps")]
        [ReadOnly]
        [JsonPropertyName("created_date")]
        public DateTimeOffset CreatedDate { get; set; }

        [Column(Order = 17, Type = "option<datetime>")]
        [JsonPropertyName("completed_date")]
        public DateTimeOffset? CompletedDate { get; set; }

        [Column(Order = 18, Type = "option<string>")]
        [FieldGroup("Holon-asset link (economic-primitive-nodes)")]
        [JsonPropertyName("asset_id")]
        public string? AssetId { get; set; }

        [Column(Order = 19, Type = "option<string>")]
        [JsonPropertyName("tx_hash")]
        public string? TxHash { get; set; }

        [Column(Order = 20)]
        [References(typeof(Holon), Optional = true)]
        [JsonPropertyName("holon_id")]
        public string? HolonId { get; set; }
    }
}
