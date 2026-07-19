// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the bridge_tx table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("bridge_tx",
        Aggregate = "BridgeTransactionResult (Models/Responses/BridgeTransactionResult.cs)",
        Guardrail = "G6 SCHEMAFULL, G2 idempotency unique index")]
    [SurrealNote("State machine: trusted Initiated->Locking->Locked->Redeeming->Completed; Wormhole Locked->AwaitingVAA->VAAReady->Redeeming->Completed; terminal Failed, Refunded, Reversing.")]
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
            Initiated, Locking, Locked, AwaitingVAA, VAAReady, Redeeming, Completed,
            Failed, Refunded, Reversing,
        }

        public enum ModeKind
        {
            Trusted,
            Wormhole,
        }

        /// <summary>Mirrors <see cref="AZOA.WebAPI.Core.ChainNetwork"/> — ALL values must stay in sync. See AGENTS.md §BridgeTx.</summary>
        public enum NetworkKind
        {
            Devnet,
            Testnet,
            Mainnet,
        }

        [Id]
        [FieldGroup("Core identity")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [References(typeof(Avatar))]
        public string AvatarId { get; set; } = string.Empty;

        [FieldGroup("Route")]
        [Required(NotEmpty = true)]
        public string SourceChain { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string TargetChain { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string SourceTokenId { get; set; } = string.Empty;

        public string? TargetTokenId { get; set; }

        [Required(NotEmpty = true)]
        public string SourceAddress { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string TargetAddress { get; set; } = string.Empty;

        [FieldGroup("Amount (string for arbitrary precision)")]
        [Required(NotEmpty = true)]
        public string Amount { get; set; } = string.Empty;

        [FieldGroup("State machine")]
        [Inside("Initiated", "Locking", "Locked", "AwaitingVAA", "VAAReady", "Redeeming", "Completed",
                "Failed", "Refunded", "Reversing")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public StatusKind Status { get; set; }

        [FieldGroup("Mode")]
        [Inside("Trusted", "Wormhole")]
        [Default("\"Trusted\"")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ModeKind Mode { get; set; }

        [FieldGroup("Transaction hashes (nullable)")]
        public string? LockTxHash { get; set; }

        public string? MintTxHash { get; set; }

        public string? ProofData { get; set; }

        public string? ErrorMessage { get; set; }

        [FieldGroup("Timestamps")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset? CompletedAt { get; set; }

        [FieldGroup("Wormhole-specific (populated when mode == Wormhole)")]
        public long? WormholeEmitterChainId { get; set; }

        [Assert("$value = NONE OR string::matches($value, \"^[0-9a-f]{64}$\")")]
        public string? WormholeEmitterAddress { get; set; }

        public long? WormholeSequence { get; set; }

        public string? VaaBytes { get; set; }

        public long? VaaSignatureCount { get; set; }

        public string? RedemptionTxHash { get; set; }

        [FieldGroup("Exactly-once / atomic-transition safety (G2)")]
        public string? IdempotencyKey { get; set; }

        [FieldGroup("Network (nullable — absent on rows written before this field existed)")]
        [Assert("$value = NONE OR $value INSIDE [\"Devnet\", \"Testnet\", \"Mainnet\"]")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public NetworkKind? Network { get; set; }
    }
}
