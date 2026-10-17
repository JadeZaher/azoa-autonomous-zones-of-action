-- ============================================================
-- Table: fungible_creation_projection
-- Aggregate: Fungible creation
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS fungible_creation_projection SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE fungible_creation_projection TYPE string;
DEFINE FIELD IF NOT EXISTS authority_id ON TABLE fungible_creation_projection TYPE string;
DEFINE FIELD IF NOT EXISTS request_hash ON TABLE fungible_creation_projection TYPE string;
DEFINE FIELD IF NOT EXISTS holon_id ON TABLE fungible_creation_projection TYPE option<string>;
DEFINE FIELD IF NOT EXISTS initial_attachment_state ON TABLE fungible_creation_projection TYPE string;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE fungible_creation_projection TYPE datetime;
