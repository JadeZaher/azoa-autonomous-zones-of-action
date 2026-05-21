-- ============================================================
-- Table: consumed_vaa_ledger
-- Aggregate: ConsumedVaaRecord (Models/ConsumedVaaRecord.cs)
-- Guardrail: G6 SCHEMAFULL, G2 replay-protection unique index on digest
-- Note: Append-only ledger of Wormhole VAAs redeemed on the target chain. Insert-wins -- duplicate digest fails UNIQUE -> replay rejected.
-- ============================================================

DEFINE TABLE consumed_vaa_ledger SCHEMAFULL;

DEFINE FIELD id ON TABLE consumed_vaa_ledger TYPE string
    ASSERT $value != NONE AND $value != "";

-- Digest is the replay-protection key (keccak256 of VAA body, hex)
DEFINE FIELD digest ON TABLE consumed_vaa_ledger TYPE string
    ASSERT $value != NONE AND $value != "";

-- Wormhole emitter chain ID
DEFINE FIELD emitter_chain_id ON TABLE consumed_vaa_ledger TYPE int
    ASSERT $value != NONE;

-- Wormhole emitter address (hex, 32-byte Wormhole format)
DEFINE FIELD emitter_address ON TABLE consumed_vaa_ledger TYPE string
    ASSERT $value != NONE AND $value != "";

-- Wormhole sequence number
DEFINE FIELD sequence ON TABLE consumed_vaa_ledger TYPE int
    ASSERT $value != NONE;

-- Bridge transaction this VAA was consumed for (audit linkage)
DEFINE FIELD bridge_transaction_id ON TABLE consumed_vaa_ledger TYPE option<string>;

-- Timestamp of consumption (immutable after insert)
DEFINE FIELD consumed_at ON TABLE consumed_vaa_ledger TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX consumed_vaa_digest
    ON TABLE consumed_vaa_ledger
    FIELDS digest
    UNIQUE;

DEFINE INDEX consumed_vaa_emitter_sequence
    ON TABLE consumed_vaa_ledger
    FIELDS emitter_chain_id, sequence
    UNIQUE;

DEFINE INDEX consumed_vaa_bridge_tx
    ON TABLE consumed_vaa_ledger
    FIELDS bridge_transaction_id;
