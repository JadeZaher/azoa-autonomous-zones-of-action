-- ============================================================
-- Table: action_run_source
-- Aggregate: Frozen workflow source admission
-- ============================================================

DEFINE TABLE IF NOT EXISTS action_run_source SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE action_run_source TYPE string;
DEFINE FIELD IF NOT EXISTS run_id ON TABLE action_run_source TYPE record<action_run>;
DEFINE FIELD IF NOT EXISTS action_id ON TABLE action_run_source TYPE record<action>;
DEFINE FIELD IF NOT EXISTS source_version ON TABLE action_run_source TYPE int;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS action_run_source_identity
    ON TABLE action_run_source
    FIELDS run_id, action_id
    UNIQUE;
