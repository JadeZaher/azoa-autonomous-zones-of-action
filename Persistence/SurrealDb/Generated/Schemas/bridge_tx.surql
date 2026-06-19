-- ============================================================
-- Table: bridge_tx
-- Aggregate: BridgeTransactionResult (Models/Responses/BridgeTransactionResult.cs)
-- Guardrail: G6 SCHEMAFULL, G2 idempotency unique index
-- Note: State machine: Initiated->Locked->AwaitingVAA->VAAReady->Redeeming->Completed; terminal Failed, Refunded, Reversing.
-- Note: B2 fix: bridge_tx_wormhole_vaa UNIQUE on (wormhole_emitter_chain_id, wormhole_emitter_address, wormhole_sequence) is the canonical Wormhole VAA dedup key. Mirrors consumed_vaa_ledger.consumed_vaa_emitter_address_sequence so a VAA cannot be replayed via the bridge_tx path either. NULL-collision caveat: all three fields are option<...>; rows in modes != Wormhole legitimately leave them NULL and SurrealDB UNIQUE-on-nullable does NOT collide NULLs, so non-Wormhole rows do not contend on this index.
-- Note: B3 review: bridge_tx_idempotency_key UNIQUE on option<string> -- NULL rows do NOT collide. The bridge claim flow MUST supply an idempotency_key (validator enforced in api-safety-hardening §4); rows without a key are legacy/diagnostic only and the adapter never inserts NULL via the claim path.
-- Note: B3 review: bridge_tx_lock_route UNIQUE on (source_chain, lock_tx_hash, target_chain) where lock_tx_hash is option<string>. Pre-lock rows legitimately have NULL lock_tx_hash; SurrealDB will not collide NULLs so multiple Initiated rows can coexist. Replay-dedup applies only once lock_tx_hash is set (post-lock state).
-- Note: MEDIUM #M4 fix: wormhole_emitter_address is asserted to be the canonical Wormhole 32-byte hex form -- 64 lowercase hex chars, no 0x prefix. Wormhole spec stores emitter addresses padded to 32 bytes regardless of source chain (EVM 20-byte addresses are left-zero-padded). Storing some rows in raw 20-byte form and others in 32-byte form would silently bypass the bridge_tx_wormhole_vaa UNIQUE dedup key for cross-emitter replay. Application code must canonicalize on insert; this ASSERT is the last line of defence.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- BridgeTx.StatusKind
DEFINE PARAM IF NOT EXISTS $bridge_tx_status VALUE ["Initiated", "Locked", "AwaitingVAA", "VAAReady", "Redeeming", "Completed", "Failed", "Refunded", "Reversing"];
-- BridgeTx.ModeKind
DEFINE PARAM IF NOT EXISTS $bridge_tx_mode VALUE ["Trusted", "Wormhole"];

DEFINE TABLE IF NOT EXISTS bridge_tx SCHEMAFULL;

-- Core identity
DEFINE FIELD IF NOT EXISTS id ON TABLE bridge_tx TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS avatar_id ON TABLE bridge_tx TYPE record<avatar>;

-- Route
DEFINE FIELD IF NOT EXISTS source_chain ON TABLE bridge_tx TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS target_chain ON TABLE bridge_tx TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS source_token_id ON TABLE bridge_tx TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS target_token_id ON TABLE bridge_tx TYPE option<string>;
DEFINE FIELD IF NOT EXISTS source_address ON TABLE bridge_tx TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS target_address ON TABLE bridge_tx TYPE string
    ASSERT $value != NONE AND $value != "";

-- Amount (string for arbitrary precision)
DEFINE FIELD IF NOT EXISTS amount ON TABLE bridge_tx TYPE string
    ASSERT $value != NONE AND $value != "";

-- State machine
DEFINE FIELD IF NOT EXISTS status ON TABLE bridge_tx TYPE string
    ASSERT $value INSIDE $bridge_tx_status;

-- Mode
DEFINE FIELD IF NOT EXISTS mode ON TABLE bridge_tx TYPE string
    ASSERT $value INSIDE $bridge_tx_mode
    DEFAULT "Trusted";

-- Transaction hashes (nullable)
DEFINE FIELD IF NOT EXISTS lock_tx_hash ON TABLE bridge_tx TYPE option<string>;
DEFINE FIELD IF NOT EXISTS mint_tx_hash ON TABLE bridge_tx TYPE option<string>;
DEFINE FIELD IF NOT EXISTS proof_data ON TABLE bridge_tx TYPE option<string>;
DEFINE FIELD IF NOT EXISTS error_message ON TABLE bridge_tx TYPE option<string>;

-- Timestamps
DEFINE FIELD IF NOT EXISTS created_at ON TABLE bridge_tx TYPE datetime
    READONLY;
DEFINE FIELD IF NOT EXISTS completed_at ON TABLE bridge_tx TYPE option<datetime>;

-- Wormhole-specific (populated when mode == Wormhole)
DEFINE FIELD IF NOT EXISTS wormhole_emitter_chain_id ON TABLE bridge_tx TYPE option<int>;
DEFINE FIELD IF NOT EXISTS wormhole_emitter_address ON TABLE bridge_tx TYPE option<string>
    ASSERT $value = NONE OR string::matches($value, "^[0-9a-f]{64}$");
DEFINE FIELD IF NOT EXISTS wormhole_sequence ON TABLE bridge_tx TYPE option<int>;
DEFINE FIELD IF NOT EXISTS vaa_bytes ON TABLE bridge_tx TYPE option<string>;
DEFINE FIELD IF NOT EXISTS vaa_signature_count ON TABLE bridge_tx TYPE option<int>;
DEFINE FIELD IF NOT EXISTS redemption_tx_hash ON TABLE bridge_tx TYPE option<string>;

-- Exactly-once / atomic-transition safety (G2)
DEFINE FIELD IF NOT EXISTS idempotency_key ON TABLE bridge_tx TYPE option<string>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS bridge_tx_idempotency_key
    ON TABLE bridge_tx
    FIELDS idempotency_key
    UNIQUE;

DEFINE INDEX IF NOT EXISTS bridge_tx_lock_route
    ON TABLE bridge_tx
    FIELDS source_chain, lock_tx_hash, target_chain
    UNIQUE;

DEFINE INDEX IF NOT EXISTS bridge_tx_wormhole_vaa
    ON TABLE bridge_tx
    FIELDS wormhole_emitter_chain_id, wormhole_emitter_address, wormhole_sequence
    UNIQUE;

DEFINE INDEX IF NOT EXISTS bridge_tx_avatar_id
    ON TABLE bridge_tx
    FIELDS avatar_id;

DEFINE INDEX IF NOT EXISTS bridge_tx_status
    ON TABLE bridge_tx
    FIELDS status;
