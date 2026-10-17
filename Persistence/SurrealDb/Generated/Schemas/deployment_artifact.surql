-- ============================================================
-- Table: deployment_artifact
-- Aggregate: Deployment
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS deployment_artifact SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE deployment_artifact TYPE string;
DEFINE FIELD IF NOT EXISTS owner_user_id ON TABLE deployment_artifact TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE deployment_artifact TYPE string;
DEFINE FIELD IF NOT EXISTS bundle_id ON TABLE deployment_artifact TYPE string;
DEFINE FIELD IF NOT EXISTS payload_json ON TABLE deployment_artifact TYPE string;
DEFINE FIELD IF NOT EXISTS payload_hash ON TABLE deployment_artifact TYPE string;
DEFINE FIELD IF NOT EXISTS artifact_hash ON TABLE deployment_artifact TYPE string;
DEFINE FIELD IF NOT EXISTS loader ON TABLE deployment_artifact TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE deployment_artifact TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS deployment_artifact_owner
    ON TABLE deployment_artifact
    FIELDS owner_user_id, created_at;
