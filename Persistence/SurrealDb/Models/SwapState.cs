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

        [Id]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [References(typeof(Avatar))]
        public string AvatarId { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string Chain { get; set; } = string.Empty;

        [FieldGroup("Source/target token pair")]
        [Required(NotEmpty = true)]
        public string TokenIn { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string TokenOut { get; set; } = string.Empty;

        [FieldGroup("Amounts (strings -- arbitrary precision)")]
        [Required(NotEmpty = true)]
        public string AmountIn { get; set; } = string.Empty;

        [Optional]
        public string? ExpectedAmountOut { get; set; }

        [Optional]
        public string? ActualAmountOut { get; set; }

        [FieldGroup("Slippage in basis points")]
        [Default("50")]
        public long SlippageBps { get; set; }

        [Optional]
        [FieldGroup("Wallet executing the swap")]
        public string? WalletAddress { get; set; }

        [Optional]
        [FieldGroup("Quote reference (chain-side opaque)")]
        public string? QuoteId { get; set; }

        [FieldGroup("Status (OperationStatus constants)")]
        [Inside("Pending", "Unknown", "Failed", "Completed", "AwaitingSignature",
                "Minted", "Burned", "Exchanged", "Swapped", "Transferred", "Deployed", "Called")]
        [Default("\"Pending\"")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public StatusKind Status { get; set; }

        [Optional]
        [FieldGroup("G2 idempotency")]
        public string? IdempotencyKey { get; set; }

        [Optional]
        public string? ErrorMessage { get; set; }

        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public DateTimeOffset? CompletedAt { get; set; }
    }
}
