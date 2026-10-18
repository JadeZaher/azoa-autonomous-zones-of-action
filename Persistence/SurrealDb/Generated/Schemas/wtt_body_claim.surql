-- ============================================================
-- Table: wtt_body_claim
-- Aggregate: Wtt
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS wtt_body_claim SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE wtt_body_claim TYPE string;
DEFINE FIELD IF NOT EXISTS target_partition ON TABLE wtt_body_claim TYPE string;
DEFINE FIELD IF NOT EXISTS target_protocol_chain ON TABLE wtt_body_claim TYPE int;
DEFINE FIELD IF NOT EXISTS target_genesis_hex ON TABLE wtt_body_claim TYPE string;
DEFINE FIELD IF NOT EXISTS target_core_bridge_hex ON TABLE wtt_body_claim TYPE string;
DEFINE FIELD IF NOT EXISTS target_token_bridge_hex ON TABLE wtt_body_claim TYPE string;
DEFINE FIELD IF NOT EXISTS target_protocol_identity ON TABLE wtt_body_claim TYPE string;
DEFINE FIELD IF NOT EXISTS target_core_bridge ON TABLE wtt_body_claim TYPE string;
DEFINE FIELD IF NOT EXISTS target_token_bridge ON TABLE wtt_body_claim TYPE string;
DEFINE FIELD IF NOT EXISTS source_protocol_identity ON TABLE wtt_body_claim TYPE string;
DEFINE FIELD IF NOT EXISTS emitter_chain ON TABLE wtt_body_claim TYPE int;
DEFINE FIELD IF NOT EXISTS emitter_address ON TABLE wtt_body_claim TYPE string;
DEFINE FIELD IF NOT EXISTS sequence ON TABLE wtt_body_claim TYPE string;
DEFINE FIELD IF NOT EXISTS body_digest ON TABLE wtt_body_claim TYPE string;
DEFINE FIELD IF NOT EXISTS body_hex ON TABLE wtt_body_claim TYPE string;
DEFINE FIELD IF NOT EXISTS operation_id ON TABLE wtt_body_claim TYPE string;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE wtt_body_claim TYPE string;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE wtt_body_claim TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE wtt_body_claim TYPE string;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE wtt_body_claim TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS wtt_publication_identity
    ON TABLE wtt_body_claim
    FIELDS target_partition, emitter_chain, emitter_address, sequence
    UNIQUE;
