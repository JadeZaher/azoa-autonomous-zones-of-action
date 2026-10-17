-- ============================================================
-- Table: wtt_preparation
-- Aggregate: Wtt
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS wtt_preparation SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE wtt_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS binding_json ON TABLE wtt_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS plan_json ON TABLE wtt_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS signed_preparation_json ON TABLE wtt_preparation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS sealed ON TABLE wtt_preparation TYPE bool;
DEFINE FIELD IF NOT EXISTS version ON TABLE wtt_preparation TYPE int;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE wtt_preparation TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE wtt_preparation TYPE datetime;
