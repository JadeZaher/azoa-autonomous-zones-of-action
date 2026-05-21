-- ============================================================
-- Table: bridge_tx
-- Aggregate: BridgeTransactionResult (Models/Responses/BridgeTransactionResult.cs)
-- Guardrail: G6 SCHEMAFULL, G2 idempotency unique index
-- Note: State machine: Initiated->Locked->AwaitingVAA->VAAReady->Redeeming->Completed; terminal Failed, Refunded, Reversing.
-- Note: B2 fix: bridge_tx_wormhole_vaa UNIQUE on (wormhole_emitter_chain_id, wormhole_emitter_address, wormhole_sequence) is the canonical Wormhole VAA dedup key. Mirrors consumed_vaa_ledger.consumed_vaa_emitter_address_sequence so a VAA cannot be replayed via the bridge_tx path either. NULL-collision caveat: all three fields are option<...>; rows in modes != Wormhole legitimately leave them NULL and SurrealDB UNIQUE-on-nullable does NOT collide NULLs, so non-Wormhole rows do not contend on this index.
-- Note: B3 review: bridge_tx_idempotency_key UNIQUE on option<string> -- NULL rows do NOT collide. The bridge claim flow MUST supply an idempotency_key (validator enforced in api-safety-hardening §4); rows without a key are legacy/diagnostic only and the adapter never inserts NULL via the claim path.
-- Note: B3 review: bridge_tx_lock_route UNIQUE on (source_chain, lock_tx_hash, target_chain) where lock_tx_hash is option<string>. Pre-lock rows legitimately have NULL lock_tx_hash; SurrealDB will not collide NULLs so multiple Initiated rows can coexist. Replay-dedup applies only once lock_tx_hash is set (post-lock state).
-- ============================================================

DEFINE TABLE bridge_tx SCHEMAFULL;

-- Core identity
DEFINE FIELD id ON TABLE bridge_tx TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD avatar_id ON TABLE bridge_tx TYPE string
    ASSERT $value != NONE AND $value != "";

-- Route
DEFINE FIELD source_chain ON TABLE bridge_tx TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD target_chain ON TABLE bridge_tx TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD source_token_id ON TABLE bridge_tx TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD target_token_id ON TABLE bridge_tx TYPE option<string>;
DEFINE FIELD source_address ON TABLE bridge_tx TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD target_address ON TABLE bridge_tx TYPE string
    ASSERT $value != NONE AND $value != "";

-- Amount (string for arbitrary precision)
DEFINE FIELD amount ON TABLE bridge_tx TYPE string
    ASSERT $value != NONE AND $value != "";

-- State machine
DEFINE FIELD status ON TABLE bridge_tx TYPE string
    ASSERT $value INSIDE ["Initiated", "Locked", "AwaitingVAA", "VAAReady", "Redeeming", "Completed", "Failed", "Refunded", "Reversing"];

-- Mode
DEFINE FIELD mode ON TABLE bridge_tx TYPE string
    ASSERT $value INSIDE ["Trusted", "Wormhole"]
    DEFAULT "Trusted";

-- Transaction hashes (nullable)
DEFINE FIELD lock_tx_hash ON TABLE bridge_tx TYPE option<string>;
DEFINE FIELD mint_tx_hash ON TABLE bridge_tx TYPE option<string>;
DEFINE FIELD proof_data ON TABLE bridge_tx TYPE option<string>;
DEFINE FIELD error_message ON TABLE bridge_tx TYPE option<string>;

-- Timestamps
DEFINE FIELD created_at ON TABLE bridge_tx TYPE datetime;
DEFINE FIELD completed_at ON TABLE bridge_tx TYPE option<datetime>;

-- Wormhole-specific (populated when mode == Wormhole)
DEFINE FIELD wormhole_emitter_chain_id ON TABLE bridge_tx TYPE option<int>;
DEFINE FIELD wormhole_emitter_address ON TABLE bridge_tx TYPE option<string>;
DEFINE FIELD wormhole_sequence ON TABLE bridge_tx TYPE option<int>;
DEFINE FIELD vaa_bytes ON TABLE bridge_tx TYPE option<string>;
DEFINE FIELD vaa_signature_count ON TABLE bridge_tx TYPE option<int>;
DEFINE FIELD redemption_tx_hash ON TABLE bridge_tx TYPE option<string>;

-- Exactly-once / atomic-transition safety (G2)
DEFINE FIELD idempotency_key ON TABLE bridge_tx TYPE option<string>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX bridge_tx_idempotency_key
    ON TABLE bridge_tx
    FIELDS idempotency_key
    UNIQUE;

DEFINE INDEX bridge_tx_lock_route
    ON TABLE bridge_tx
    FIELDS source_chain, lock_tx_hash, target_chain
    UNIQUE;

DEFINE INDEX bridge_tx_wormhole_vaa
    ON TABLE bridge_tx
    FIELDS wormhole_emitter_chain_id, wormhole_emitter_address, wormhole_sequence
    UNIQUE;

DEFINE INDEX bridge_tx_avatar_id
    ON TABLE bridge_tx
    FIELDS avatar_id;

DEFINE INDEX bridge_tx_status
    ON TABLE bridge_tx
    FIELDS status;
