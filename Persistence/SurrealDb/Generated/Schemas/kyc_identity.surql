-- ============================================================
-- Table: kyc_identity
-- Aggregate: KycIdentity (node-operator-only person<->avatar binding)
-- Guardrail: G6 SCHEMAFULL
-- Note: NODE-OPERATOR-ONLY. Deliberately absent from SearchableEntityType, HolonQueryRequest, and the MCP avatar_scoped_query table allow-list; see Persistence/SurrealDb/Models/AGENTS.md #kyc-identity-binding.
-- Note: Stores a salted HMAC of document_number + issuing_state, never the raw document identifiers, and never address, personal_number, or any media/biometric material.
-- Note: Uniqueness is (tenant_scope, person_key). There is deliberately NO unique index on person_key alone: one human resolves to an unlinkable key per tenant (D23).
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- KycIdentity.KycProvider
DEFINE PARAM IF NOT EXISTS $kyc_identity_provider VALUE ["MANUAL", "VERIFF", "GENERIC_HOSTED", "IDENTOMAT"];

DEFINE TABLE IF NOT EXISTS kyc_identity SCHEMAFULL;

-- Deterministic record id (SHA-256 of tenant_scope + person_key)
DEFINE FIELD IF NOT EXISTS id ON TABLE kyc_identity TYPE string
    ASSERT $value != NONE AND $value != "";

-- Uniqueness scope: per-tenant by default, node-global only on explicit opt-in
DEFINE FIELD IF NOT EXISTS tenant_scope ON TABLE kyc_identity TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE kyc_identity TYPE option<record<avatar>>;

-- Pseudonymous person keys (never the raw document identifiers)
DEFINE FIELD IF NOT EXISTS person_key ON TABLE kyc_identity TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS global_person_key ON TABLE kyc_identity TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS person_key_version ON TABLE kyc_identity TYPE int
    DEFAULT 1;

-- Person -> avatars link (NEVER projected to a consumer)
DEFINE FIELD IF NOT EXISTS avatar_ids ON TABLE kyc_identity TYPE option<array<string>>;

-- Retained personal fields (the ONLY PII kept; tombstoned on erasure)
DEFINE FIELD IF NOT EXISTS first_name ON TABLE kyc_identity TYPE option<string>;
DEFINE FIELD IF NOT EXISTS last_name ON TABLE kyc_identity TYPE option<string>;
DEFINE FIELD IF NOT EXISTS birthday ON TABLE kyc_identity TYPE option<string>;
DEFINE FIELD IF NOT EXISTS citizenship ON TABLE kyc_identity TYPE option<string>;

-- Provenance
DEFINE FIELD IF NOT EXISTS provider ON TABLE kyc_identity TYPE string
    ASSERT $value INSIDE $kyc_identity_provider;
DEFINE FIELD IF NOT EXISTS provider_key ON TABLE kyc_identity TYPE string;

-- Retention + erasure (GDPR Art. 17 vs KYC record-keeping)
DEFINE FIELD IF NOT EXISTS retention_expires_at ON TABLE kyc_identity TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS erased_at ON TABLE kyc_identity TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS erasure_reason ON TABLE kyc_identity TYPE option<string>;

-- Timestamps
DEFINE FIELD IF NOT EXISTS created_date ON TABLE kyc_identity TYPE datetime
    DEFAULT time::now()
    READONLY;
DEFINE FIELD IF NOT EXISTS modified_date ON TABLE kyc_identity TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS kyc_identity_scope_person
    ON TABLE kyc_identity
    FIELDS tenant_scope, person_key
    UNIQUE;

DEFINE INDEX IF NOT EXISTS kyc_identity_global_person
    ON TABLE kyc_identity
    FIELDS global_person_key;

DEFINE INDEX IF NOT EXISTS kyc_identity_retention
    ON TABLE kyc_identity
    FIELDS retention_expires_at;
