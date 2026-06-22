// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the consumed_vaa_ledger table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("consumed_vaa_ledger",
        Aggregate = "ConsumedVaaRecord (Models/ConsumedVaaRecord.cs)",
        Guardrail = "G6 SCHEMAFULL, G2 replay-protection unique index on digest")]
    [SurrealNote("Append-only ledger of Wormhole VAAs redeemed on the target chain. Insert-wins -- duplicate digest fails UNIQUE -> replay rejected.")]
    [SurrealNote("B2 fix: consumed_vaa_emitter_address_sequence UNIQUE on (emitter_chain_id, emitter_address, sequence) is the canonical Wormhole VAA dedup tuple. This MIRRORS bridge_tx.bridge_tx_wormhole_vaa so the same VAA cannot be redeemed twice via either path. Wave-1 had only (emitter_chain_id, sequence) which collapses the dedup key across distinct emitters on the same chain -- a bug for chains hosting multiple Wormhole-compatible emitters.")]
    [SurrealNote("B3 review: all UNIQUE INDEX fields here are NOT NULL (digest, emitter_chain_id, emitter_address, sequence all ASSERT != NONE), so SurrealDB NULL-collision semantics are not in play.")]
    [SurrealNote("MEDIUM #M4 fix: emitter_address is asserted to be the canonical Wormhole 32-byte hex form -- 64 lowercase hex chars, no 0x prefix. Wormhole pads EVM 20-byte addresses to 32 bytes; storing some rows raw and others padded would silently bypass the consumed_vaa_emitter_address_sequence UNIQUE dedup key. Application code must canonicalize on insert; this ASSERT is the last line of defence.")]
    [Slice("bridge")]
    [Index("consumed_vaa_digest", Fields = new[] { "digest" }, Unique = true)]
    [Index("consumed_vaa_emitter_address_sequence", Fields = new[] { "emitter_chain_id", "emitter_address", "sequence" }, Unique = true)]
    [Index("consumed_vaa_bridge_tx", Fields = new[] { "bridge_transaction_id" })]
    public partial class ConsumedVaaLedger : ISurrealRecord
    {
        public const string SchemaNameConst = "consumed_vaa_ledger";
        public string SchemaName => SchemaNameConst;

        [Id]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Digest is the replay-protection key (keccak256 of VAA body, hex)")]
        [Required(NotEmpty = true)]
        public string Digest { get; set; } = string.Empty;

        [FieldGroup("Wormhole emitter chain ID")]
        [Assert("$value != NONE")]
        public long EmitterChainId { get; set; }

        [FieldGroup("Wormhole emitter address (hex, 32-byte Wormhole format)")]
        [Assert("$value != NONE AND $value != \"\" AND string::matches($value, \"^[0-9a-f]{64}$\")")]
        public string EmitterAddress { get; set; } = string.Empty;

        [FieldGroup("Wormhole sequence number")]
        [Assert("$value != NONE")]
        public long Sequence { get; set; }

        [FieldGroup("Bridge transaction this VAA was consumed for (audit linkage)")]
        [References(typeof(BridgeTx), Optional = true)]
        public string? BridgeTransactionId { get; set; }

        [FieldGroup("Timestamp of consumption (immutable after insert)")]
        [ReadOnly]
        public DateTimeOffset ConsumedAt { get; set; }
    }
}
