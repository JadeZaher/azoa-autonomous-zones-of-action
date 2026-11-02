-- ============================================================
-- Table: member_handoff
-- Aggregate: MemberHandoff
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS member_handoff SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE member_handoff TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE member_handoff TYPE string;
DEFINE FIELD IF NOT EXISTS target_user_id ON TABLE member_handoff TYPE string;
DEFINE FIELD IF NOT EXISTS external_subject ON TABLE member_handoff TYPE string;
DEFINE FIELD IF NOT EXISTS descriptor ON TABLE member_handoff TYPE string;
DEFINE FIELD IF NOT EXISTS delivery_email_digest ON TABLE member_handoff TYPE string;
DEFINE FIELD IF NOT EXISTS protected_delivery ON TABLE member_handoff TYPE string;
DEFINE FIELD IF NOT EXISTS token_digest ON TABLE member_handoff TYPE option<string>;
DEFINE FIELD IF NOT EXISTS status ON TABLE member_handoff TYPE string;
DEFINE FIELD IF NOT EXISTS expires_at ON TABLE member_handoff TYPE datetime;
DEFINE FIELD IF NOT EXISTS version ON TABLE member_handoff TYPE int;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE member_handoff TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS member_handoff_target
    ON TABLE member_handoff
    FIELDS tenant_id, target_user_id;
