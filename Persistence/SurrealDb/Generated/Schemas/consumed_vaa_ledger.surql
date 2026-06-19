-- ============================================================
-- Table: consumed_vaa_ledger
-- Aggregate: ConsumedVaaRecord (Models/ConsumedVaaRecord.cs)
-- Guardrail: G6 SCHEMAFULL, G2 replay-protection unique index on digest
-- Note: Append-only ledger of Wormhole VAAs redeemed on the target chain. Insert-wins -- duplicate digest fails UNIQUE -> replay rejected.
-- Note: B2 fix: consumed_vaa_emitter_address_sequence UNIQUE on (emitter_chain_id, emitter_address, sequence) is the canonical Wormhole VAA dedup tuple. This MIRRORS bridge_tx.bridge_tx_wormhole_vaa so the same VAA cannot be redeemed twice via either path. Wave-1 had only (emitter_chain_id, sequence) which collapses the dedup key across distinct emitters on the same chain -- a bug for chains hosting multiple Wormhole-compatible emitters.
-- Note: B3 review: all UNIQUE INDEX fields here are NOT NULL (digest, emitter_chain_id, emitter_address, sequence all ASSERT != NONE), so SurrealDB NULL-collision semantics are not in play.
-- Note: MEDIUM #M4 fix: emitter_address is asserted to be the canonical Wormhole 32-byte hex form -- 64 lowercase hex chars, no 0x prefix. Wormhole pads EVM 20-byte addresses to 32 bytes; storing some rows raw and others padded would silently bypass the consumed_vaa_emitter_address_sequence UNIQUE dedup key. Application code must canonicalize on insert; this ASSERT is the last line of defence.
-- ============================================================

DEFINE TABLE IF NOT EXISTS consumed_vaa_ledger SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE consumed_vaa_ledger TYPE string
    ASSERT $value != NONE AND $value != "";

-- Digest is the replay-protection key (keccak256 of VAA body, hex)
DEFINE FIELD IF NOT EXISTS digest ON TABLE consumed_vaa_ledger TYPE string
    ASSERT $value != NONE AND $value != "";

-- Wormhole emitter chain ID
DEFINE FIELD IF NOT EXISTS emitter_chain_id ON TABLE consumed_vaa_ledger TYPE int
    ASSERT $value != NONE;

-- Wormhole emitter address (hex, 32-byte Wormhole format)
DEFINE FIELD IF NOT EXISTS emitter_address ON TABLE consumed_vaa_ledger TYPE string
    ASSERT $value != NONE AND $value != "" AND string::matches($value, "^[0-9a-f]{64}$");

-- Wormhole sequence number
DEFINE FIELD IF NOT EXISTS sequence ON TABLE consumed_vaa_ledger TYPE int
    ASSERT $value != NONE;

-- Bridge transaction this VAA was consumed for (audit linkage)
DEFINE FIELD IF NOT EXISTS bridge_transaction_id ON TABLE consumed_vaa_ledger TYPE option<record<bridge_tx>>;

-- Timestamp of consumption (immutable after insert)
DEFINE FIELD IF NOT EXISTS consumed_at ON TABLE consumed_vaa_ledger TYPE datetime
    READONLY;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS consumed_vaa_digest
    ON TABLE consumed_vaa_ledger
    FIELDS digest
    UNIQUE;

DEFINE INDEX IF NOT EXISTS consumed_vaa_emitter_address_sequence
    ON TABLE consumed_vaa_ledger
    FIELDS emitter_chain_id, emitter_address, sequence
    UNIQUE;

DEFINE INDEX IF NOT EXISTS consumed_vaa_bridge_tx
    ON TABLE consumed_vaa_ledger
    FIELDS bridge_transaction_id;
