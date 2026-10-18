-- ============================================================
-- Table: fungible_creation_attachment
-- Aggregate: Fungible creation
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS fungible_creation_attachment SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE fungible_creation_attachment TYPE string;
DEFINE FIELD IF NOT EXISTS operation_id ON TABLE fungible_creation_attachment TYPE string;
DEFINE FIELD IF NOT EXISTS projection_authority_id ON TABLE fungible_creation_attachment TYPE string;
DEFINE FIELD IF NOT EXISTS holon_id ON TABLE fungible_creation_attachment TYPE string;
DEFINE FIELD IF NOT EXISTS actor_id ON TABLE fungible_creation_attachment TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE fungible_creation_attachment TYPE option<string>;
DEFINE FIELD IF NOT EXISTS expected_version ON TABLE fungible_creation_attachment TYPE int;
DEFINE FIELD IF NOT EXISTS request_hash ON TABLE fungible_creation_attachment TYPE string;
DEFINE FIELD IF NOT EXISTS consent_json ON TABLE fungible_creation_attachment TYPE string;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE fungible_creation_attachment TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS fungible_creation_attachment_operation
    ON TABLE fungible_creation_attachment
    FIELDS operation_id
    UNIQUE;
