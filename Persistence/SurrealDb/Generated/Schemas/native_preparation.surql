-- ============================================================
-- Table: native_preparation
-- Aggregate: Deployment
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS native_preparation SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE native_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE native_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS stage ON TABLE native_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS generation ON TABLE native_preparation TYPE int;
DEFINE FIELD IF NOT EXISTS plan_json ON TABLE native_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS prepared_json ON TABLE native_preparation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE native_preparation TYPE datetime;
