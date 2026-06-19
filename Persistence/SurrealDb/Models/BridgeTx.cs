// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the bridge_tx table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("bridge_tx",
        Aggregate = "BridgeTransactionResult (Models/Responses/BridgeTransactionResult.cs)",
        Guardrail = "G6 SCHEMAFULL, G2 idempotency unique index")]
    [SurrealNote("State machine: Initiated->Locked->AwaitingVAA->VAAReady->Redeeming->Completed; terminal Failed, Refunded, Reversing.")]
    [SurrealNote("B2 fix: bridge_tx_wormhole_vaa UNIQUE on (wormhole_emitter_chain_id, wormhole_emitter_address, wormhole_sequence) is the canonical Wormhole VAA dedup key. Mirrors consumed_vaa_ledger.consumed_vaa_emitter_address_sequence so a VAA cannot be replayed via the bridge_tx path either. NULL-collision caveat: all three fields are option<...>; rows in modes != Wormhole legitimately leave them NULL and SurrealDB UNIQUE-on-nullable does NOT collide NULLs, so non-Wormhole rows do not contend on this index.")]
    [SurrealNote("B3 review: bridge_tx_idempotency_key UNIQUE on option<string> -- NULL rows do NOT collide. The bridge claim flow MUST supply an idempotency_key (validator enforced in api-safety-hardening §4); rows without a key are legacy/diagnostic only and the adapter never inserts NULL via the claim path.")]
    [SurrealNote("B3 review: bridge_tx_lock_route UNIQUE on (source_chain, lock_tx_hash, target_chain) where lock_tx_hash is option<string>. Pre-lock rows legitimately have NULL lock_tx_hash; SurrealDB will not collide NULLs so multiple Initiated rows can coexist. Replay-dedup applies only once lock_tx_hash is set (post-lock state).")]
    [SurrealNote("MEDIUM #M4 fix: wormhole_emitter_address is asserted to be the canonical Wormhole 32-byte hex form -- 64 lowercase hex chars, no 0x prefix. Wormhole spec stores emitter addresses padded to 32 bytes regardless of source chain (EVM 20-byte addresses are left-zero-padded). Storing some rows in raw 20-byte form and others in 32-byte form would silently bypass the bridge_tx_wormhole_vaa UNIQUE dedup key for cross-emitter replay. Application code must canonicalize on insert; this ASSERT is the last line of defence.")]
    [Slice("bridge")]
    [Index("bridge_tx_idempotency_key", Fields = new[] { "idempotency_key" }, Unique = true)]
    [Index("bridge_tx_lock_route", Fields = new[] { "source_chain", "lock_tx_hash", "target_chain" }, Unique = true)]
    [Index("bridge_tx_wormhole_vaa", Fields = new[] { "wormhole_emitter_chain_id", "wormhole_emitter_address", "wormhole_sequence" }, Unique = true)]
    [Index("bridge_tx_avatar_id", Fields = new[] { "avatar_id" })]
    [Index("bridge_tx_status", Fields = new[] { "status" })]
    public partial class BridgeTx : ISurrealRecord
    {
        public const string SchemaNameConst = "bridge_tx";
        public string SchemaName => SchemaNameConst;

        public enum StatusKind
        {
            Initiated, Locked, AwaitingVAA, VAAReady, Redeeming, Completed,
            Failed, Refunded, Reversing,
        }

        public enum ModeKind
        {
            Trusted,
            Wormhole,
        }

        [Id, Column(Order = 1, Type = "string")]
        [FieldGroup("Core identity")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [References(typeof(Avatar))]
        [JsonPropertyName("avatar_id")]
        public string AvatarId { get; set; } = string.Empty;

        [Column(Order = 3, Type = "string")]
        [FieldGroup("Route")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("source_chain")]
        public string SourceChain { get; set; } = string.Empty;

        [Column(Order = 4, Type = "string")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("target_chain")]
        public string TargetChain { get; set; } = string.Empty;

        [Column(Order = 5, Type = "string")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("source_token_id")]
        public string SourceTokenId { get; set; } = string.Empty;

        [Column(Order = 6, Type = "option<string>")]
        [JsonPropertyName("target_token_id")]
        public string? TargetTokenId { get; set; }

        [Column(Order = 7, Type = "string")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("source_address")]
        public string SourceAddress { get; set; } = string.Empty;

        [Column(Order = 8, Type = "string")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("target_address")]
        public string TargetAddress { get; set; } = string.Empty;

        [Column(Order = 9, Type = "string")]
        [FieldGroup("Amount (string for arbitrary precision)")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("amount")]
        public string Amount { get; set; } = string.Empty;

        [Column(Order = 10, Type = "string")]
        [FieldGroup("State machine")]
        [Inside("Initiated", "Locked", "AwaitingVAA", "VAAReady", "Redeeming", "Completed",
                "Failed", "Refunded", "Reversing")]
        [JsonPropertyName("status"), JsonConverter(typeof(JsonStringEnumConverter))]
        public StatusKind Status { get; set; }

        [Column(Order = 11, Type = "string")]
        [FieldGroup("Mode")]
        [Inside("Trusted", "Wormhole")]
        [Default("\"Trusted\"")]
        [JsonPropertyName("mode"), JsonConverter(typeof(JsonStringEnumConverter))]
        public ModeKind Mode { get; set; }

        [Column(Order = 12, Type = "option<string>")]
        [FieldGroup("Transaction hashes (nullable)")]
        [JsonPropertyName("lock_tx_hash")]
        public string? LockTxHash { get; set; }

        [Column(Order = 13, Type = "option<string>")]
        [JsonPropertyName("mint_tx_hash")]
        public string? MintTxHash { get; set; }

        [Column(Order = 14, Type = "option<string>")]
        [JsonPropertyName("proof_data")]
        public string? ProofData { get; set; }

        [Column(Order = 15, Type = "option<string>")]
        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }

        [Column(Order = 16, Type = "datetime")]
        [FieldGroup("Timestamps")]
        [ReadOnly]
        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }

        [Column(Order = 17, Type = "option<datetime>")]
        [JsonPropertyName("completed_at")]
        public DateTimeOffset? CompletedAt { get; set; }

        [Column(Order = 18, Type = "option<int>")]
        [FieldGroup("Wormhole-specific (populated when mode == Wormhole)")]
        [JsonPropertyName("wormhole_emitter_chain_id")]
        public long? WormholeEmitterChainId { get; set; }

        [Column(Order = 19, Type = "option<string>")]
        [Assert("$value = NONE OR string::matches($value, \"^[0-9a-f]{64}$\")")]
        [JsonPropertyName("wormhole_emitter_address")]
        public string? WormholeEmitterAddress { get; set; }

        [Column(Order = 20, Type = "option<int>")]
        [JsonPropertyName("wormhole_sequence")]
        public long? WormholeSequence { get; set; }

        [Column(Order = 21, Type = "option<string>")]
        [JsonPropertyName("vaa_bytes")]
        public string? VaaBytes { get; set; }

        [Column(Order = 22, Type = "option<int>")]
        [JsonPropertyName("vaa_signature_count")]
        public long? VaaSignatureCount { get; set; }

        [Column(Order = 23, Type = "option<string>")]
        [JsonPropertyName("redemption_tx_hash")]
        public string? RedemptionTxHash { get; set; }

        [Column(Order = 24, Type = "option<string>")]
        [FieldGroup("Exactly-once / atomic-transition safety (G2)")]
        [JsonPropertyName("idempotency_key")]
        public string? IdempotencyKey { get; set; }
    }
}
