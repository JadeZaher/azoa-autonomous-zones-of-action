-- ============================================================
-- Table: hosted_provider_callback
-- ============================================================

DEFINE TABLE IF NOT EXISTS hosted_provider_callback SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE hosted_provider_callback TYPE string;
DEFINE FIELD IF NOT EXISTS attempt_id ON TABLE hosted_provider_callback TYPE string;
DEFINE FIELD IF NOT EXISTS body_digest ON TABLE hosted_provider_callback TYPE string;
DEFINE FIELD IF NOT EXISTS processed ON TABLE hosted_provider_callback TYPE bool;
DEFINE FIELD IF NOT EXISTS received_at ON TABLE hosted_provider_callback TYPE datetime;
