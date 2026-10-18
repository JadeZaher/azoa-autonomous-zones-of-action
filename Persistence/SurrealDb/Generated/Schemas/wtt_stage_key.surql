-- ============================================================
-- Table: wtt_stage_key
-- Aggregate: Wtt
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS wtt_stage_key SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE wtt_stage_key TYPE string;
DEFINE FIELD IF NOT EXISTS binding_json ON TABLE wtt_stage_key TYPE string;
DEFINE FIELD IF NOT EXISTS public_key ON TABLE wtt_stage_key TYPE string;
DEFINE FIELD IF NOT EXISTS key_version ON TABLE wtt_stage_key TYPE string;
DEFINE FIELD IF NOT EXISTS envelope_json ON TABLE wtt_stage_key TYPE string;
DEFINE FIELD IF NOT EXISTS version ON TABLE wtt_stage_key TYPE int;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE wtt_stage_key TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE wtt_stage_key TYPE datetime;
