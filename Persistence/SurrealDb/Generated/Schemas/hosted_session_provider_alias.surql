-- ============================================================
-- Table: hosted_session_provider_alias
-- Aggregate: HostedSession
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS hosted_session_provider_alias SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE hosted_session_provider_alias TYPE string;
DEFINE FIELD IF NOT EXISTS hosted_session_id ON TABLE hosted_session_provider_alias TYPE string;
DEFINE FIELD IF NOT EXISTS flow_kind ON TABLE hosted_session_provider_alias TYPE string;
DEFINE FIELD IF NOT EXISTS provider_key ON TABLE hosted_session_provider_alias TYPE string;
DEFINE FIELD IF NOT EXISTS provider_session_id ON TABLE hosted_session_provider_alias TYPE string;
