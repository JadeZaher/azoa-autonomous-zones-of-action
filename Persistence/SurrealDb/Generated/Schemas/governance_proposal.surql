-- ============================================================
-- Table: governance_proposal
-- Aggregate: Immutable tenant election authority
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS governance_proposal SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE governance_proposal TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE governance_proposal TYPE string;
DEFINE FIELD IF NOT EXISTS creator_id ON TABLE governance_proposal TYPE string;
DEFINE FIELD IF NOT EXISTS definition_hash ON TABLE governance_proposal TYPE string;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE governance_proposal TYPE string;
DEFINE FIELD IF NOT EXISTS definition_json ON TABLE governance_proposal TYPE string;
DEFINE FIELD IF NOT EXISTS electorate_json ON TABLE governance_proposal TYPE string;
DEFINE FIELD IF NOT EXISTS electorate_hash ON TABLE governance_proposal TYPE string;
DEFINE FIELD IF NOT EXISTS membership_rule_version ON TABLE governance_proposal TYPE int;
DEFINE FIELD IF NOT EXISTS uniqueness_scope ON TABLE governance_proposal TYPE string;
DEFINE FIELD IF NOT EXISTS operation_key ON TABLE governance_proposal TYPE string;
DEFINE FIELD IF NOT EXISTS opens_at ON TABLE governance_proposal TYPE datetime;
DEFINE FIELD IF NOT EXISTS closes_at ON TABLE governance_proposal TYPE datetime;
DEFINE FIELD IF NOT EXISTS minimum_votes ON TABLE governance_proposal TYPE int;
DEFINE FIELD IF NOT EXISTS version ON TABLE governance_proposal TYPE int;
DEFINE FIELD IF NOT EXISTS final_result_json ON TABLE governance_proposal TYPE option<string>;
DEFINE FIELD IF NOT EXISTS finalized_at ON TABLE governance_proposal TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE governance_proposal TYPE datetime;
