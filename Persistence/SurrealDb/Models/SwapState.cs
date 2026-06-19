// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the swap_state table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("swap_state",
        Aggregate = "Swap order/quote state (SwapQuoteRequest/SwapExecuteRequest/SwapQuoteResponse)",
        Guardrail = "G6 SCHEMAFULL, G2 idempotency unique index")]
    [SurrealNote("Status uses OperationStatus constants; minimal durable state for replay + idempotency.")]
    [SurrealNote("B3 review: swap_state_idempotency_key UNIQUE on option<string>. Same NULL-collision caveat as bridge_tx: the swap-execute flow MUST supply an idempotency_key (api-safety-hardening §4 validator); NULL rows are diagnostic-only and the claim path never inserts NULL.")]
    [Slice("wallet_nft")]
    [Index("swap_state_idempotency_key", Fields = new[] { "idempotency_key" }, Unique = true)]
    [Index("swap_state_avatar_id", Fields = new[] { "avatar_id" })]
    [Index("swap_state_status", Fields = new[] { "status" })]
    public partial class SwapState : ISurrealRecord
    {
        public const string SchemaNameConst = "swap_state";
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
        [References(typeof(Avatar))]
        [JsonPropertyName("avatar_id")]
        public string AvatarId { get; set; } = string.Empty;

        [Column(Order = 3, Type = "string")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("chain")]
        public string Chain { get; set; } = string.Empty;

        [Column(Order = 4, Type = "string")]
        [FieldGroup("Source/target token pair")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("token_in")]
        public string TokenIn { get; set; } = string.Empty;

        [Column(Order = 5, Type = "string")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("token_out")]
        public string TokenOut { get; set; } = string.Empty;

        [Column(Order = 6, Type = "string")]
        [FieldGroup("Amounts (strings -- arbitrary precision)")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("amount_in")]
        public string AmountIn { get; set; } = string.Empty;

        [Column(Order = 7, Type = "option<string>")]
        [JsonPropertyName("expected_amount_out")]
        public string? ExpectedAmountOut { get; set; }

        [Column(Order = 8, Type = "option<string>")]
        [JsonPropertyName("actual_amount_out")]
        public string? ActualAmountOut { get; set; }

        [Column(Order = 9, Type = "int")]
        [FieldGroup("Slippage in basis points")]
        [Default("50")]
        [JsonPropertyName("slippage_bps")]
        public long SlippageBps { get; set; }

        [Column(Order = 10, Type = "option<string>")]
        [FieldGroup("Wallet executing the swap")]
        [JsonPropertyName("wallet_address")]
        public string? WalletAddress { get; set; }

        [Column(Order = 11, Type = "option<string>")]
        [FieldGroup("Quote reference (chain-side opaque)")]
        [JsonPropertyName("quote_id")]
        public string? QuoteId { get; set; }

        [Column(Order = 12, Type = "string")]
        [FieldGroup("Status (OperationStatus constants)")]
        [Inside("Pending", "Unknown", "Failed", "Completed", "AwaitingSignature",
                "Minted", "Burned", "Exchanged", "Swapped", "Transferred", "Deployed", "Called")]
        [Default("\"Pending\"")]
        [JsonPropertyName("status"), JsonConverter(typeof(JsonStringEnumConverter))]
        public StatusKind Status { get; set; }

        [Column(Order = 13, Type = "option<string>")]
        [FieldGroup("G2 idempotency")]
        [JsonPropertyName("idempotency_key")]
        public string? IdempotencyKey { get; set; }

        [Column(Order = 14, Type = "option<string>")]
        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }

        [Column(Order = 15, Type = "datetime")]
        [ReadOnly]
        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }

        [Column(Order = 16, Type = "datetime")]
        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }

        [Column(Order = 17, Type = "option<datetime>")]
        [JsonPropertyName("completed_at")]
        public DateTimeOffset? CompletedAt { get; set; }
    }
}
