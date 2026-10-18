-- ============================================================
-- Table: kyc_screening_result
-- Aggregate: KycIdentity (node-operator-only person<->user binding)
-- Guardrail: G6 SCHEMAFULL
-- Note: NODE-OPERATOR-ONLY, like kyc_identity. Absent from every generic consumer read surface.
-- Note: score_threshold is persisted because the provider applies it SERVER-SIDE at query time and never echoes it per hit. Without it a stored result set is unexplainable later: you cannot tell whether an empty result means 'clean' or 'threshold was too high'.
-- Note: topics is an open array<string>, never a closed enum. The vendor does not publish an exhaustive topic list, so a strict enum would reject legitimate future values.
-- Note: This row is what a value gate reads: only the LATEST result for (subject_user_id, tenant_id, approved lineage_key) with decision NO_HIT opens a value operation. A missing row, a store error, MANUAL_REVIEW, and HIT all deny — an approved identity with no AML answer must not reach value.
-- Note: query_full_name and query_birthday_time_from are retained PII on the same D25 footing as kyc_identity's four personal fields, so retention_expires_at / erased_at exist here too. Erasing a binding without erasing these would leave a name and a date of birth behind forever.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- KycScreeningResult.KycScreeningDecision
DEFINE PARAM IF NOT EXISTS $kyc_screening_result_decision VALUE ["NO_HIT", "MANUAL_REVIEW", "HIT"];

DEFINE TABLE IF NOT EXISTS kyc_screening_result SCHEMAFULL;

-- Core identity (record id is the Guid('N') of the screening run)
DEFINE FIELD IF NOT EXISTS id ON TABLE kyc_screening_result TYPE string
    ASSERT $value != NONE AND $value != "";

-- Subject + tenancy
DEFINE FIELD IF NOT EXISTS subject_user_id ON TABLE kyc_screening_result TYPE option<record<user>>;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE kyc_screening_result TYPE option<record<user>>;
DEFINE FIELD IF NOT EXISTS lineage_key ON TABLE kyc_screening_result TYPE option<string>;

-- Exact query shape submitted to the provider (retained PII)
DEFINE FIELD IF NOT EXISTS query_full_name ON TABLE kyc_screening_result TYPE option<string>;
DEFINE FIELD IF NOT EXISTS query_birthday_time_from ON TABLE kyc_screening_result TYPE option<string>;
DEFINE FIELD IF NOT EXISTS score_threshold ON TABLE kyc_screening_result TYPE int
    DEFAULT 0;

-- AZOA-derived verdict (the provider publishes no status enum)
DEFINE FIELD IF NOT EXISTS decision ON TABLE kyc_screening_result TYPE string
    ASSERT $value INSIDE $kyc_screening_result_decision;
DEFINE FIELD IF NOT EXISTS top_score ON TABLE kyc_screening_result TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS hit_count ON TABLE kyc_screening_result TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS topics ON TABLE kyc_screening_result TYPE option<array<string>>;
DEFINE FIELD IF NOT EXISTS person_ids ON TABLE kyc_screening_result TYPE option<array<string>>;
DEFINE FIELD IF NOT EXISTS monitor_id ON TABLE kyc_screening_result TYPE option<string>;
DEFINE FIELD IF NOT EXISTS policy_version ON TABLE kyc_screening_result TYPE string;
DEFINE FIELD IF NOT EXISTS results_truncated ON TABLE kyc_screening_result TYPE bool;

-- Retention + erasure (the query shape is name + date of birth)
DEFINE FIELD IF NOT EXISTS retention_expires_at ON TABLE kyc_screening_result TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS erased_at ON TABLE kyc_screening_result TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS erasure_reason ON TABLE kyc_screening_result TYPE option<string>;

-- Timestamps
DEFINE FIELD IF NOT EXISTS screened_at ON TABLE kyc_screening_result TYPE datetime
    READONLY;
DEFINE FIELD IF NOT EXISTS created_date ON TABLE kyc_screening_result TYPE datetime;
DEFINE FIELD IF NOT EXISTS modified_date ON TABLE kyc_screening_result TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS kyc_screening_result_subject
    ON TABLE kyc_screening_result
    FIELDS subject_user_id;

DEFINE INDEX IF NOT EXISTS kyc_screening_result_decision
    ON TABLE kyc_screening_result
    FIELDS decision;

DEFINE INDEX IF NOT EXISTS kyc_screening_result_retention
    ON TABLE kyc_screening_result
    FIELDS retention_expires_at;
