-- ============================================================
-- Table: wtt_evidence_progress
-- Aggregate: Wtt
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS wtt_evidence_progress SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE wtt_evidence_progress TYPE string;
DEFINE FIELD IF NOT EXISTS binding_json ON TABLE wtt_evidence_progress TYPE string;
DEFINE FIELD IF NOT EXISTS initial_state_json ON TABLE wtt_evidence_progress TYPE string;
DEFINE FIELD IF NOT EXISTS state_json ON TABLE wtt_evidence_progress TYPE string;
DEFINE FIELD IF NOT EXISTS next_cursor ON TABLE wtt_evidence_progress TYPE option<string>;
DEFINE FIELD IF NOT EXISTS seen_cursor_hashes_json ON TABLE wtt_evidence_progress TYPE string;
DEFINE FIELD IF NOT EXISTS digest ON TABLE wtt_evidence_progress TYPE string;
DEFINE FIELD IF NOT EXISTS pages ON TABLE wtt_evidence_progress TYPE int;
DEFINE FIELD IF NOT EXISTS version ON TABLE wtt_evidence_progress TYPE int;
DEFINE FIELD IF NOT EXISTS complete ON TABLE wtt_evidence_progress TYPE bool;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE wtt_evidence_progress TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE wtt_evidence_progress TYPE datetime;
