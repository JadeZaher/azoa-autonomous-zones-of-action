-- ============================================================
-- Table: person_operation_claim
-- Aggregate: PersonOperationClaim (node-operator-only per-operation Sybil ledger)
-- Guardrail: G6 SCHEMAFULL
-- Note: NODE-OPERATOR-ONLY. Deliberately absent from SearchableEntityType, HolonQueryRequest, and the MCP user_scoped_query allow-list; see Persistence/SurrealDb/Models/AGENTS.md #person-operation-claim. A perfect denial message is worthless if a consumer can list this table and read a person key it did not create.
-- Note: Stores a person KEY and nothing else about the human. No first/last name, birthday, citizenship, or document field may ever be added here: a joinable attribute on this table would re-identify the pseudonymous claim ledger.
-- Note: Uniqueness is (uniqueness_scope, operation_key, person_key). uniqueness_scope is a tenant Guid('N') by default and the node-global sentinel only on an explicit per-operation opt-in (D23).
-- Note: D39: decision_option is the VOTE OF RECORD, and it sits beside person_key — so the node operator, and only the node operator, can read person-to-choice here. That is the price of a tally nobody can forge, and the reason this table must stay off every consumer read surface. The consumer-visible ballot receipt holon carries no authority and the tally publishes counts only.
-- ============================================================

DEFINE TABLE IF NOT EXISTS person_operation_claim SCHEMAFULL;

-- Deterministic record id (SHA-256 of uniqueness_scope + operation_key + person_key)
DEFINE FIELD IF NOT EXISTS id ON TABLE person_operation_claim TYPE string
    ASSERT $value != NONE AND $value != "";

-- Claim identity: the (scope, operation, person) triple the unique index enforces
DEFINE FIELD IF NOT EXISTS uniqueness_scope ON TABLE person_operation_claim TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS operation_key ON TABLE person_operation_claim TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS person_key ON TABLE person_operation_claim TYPE string
    ASSERT $value != NONE AND $value != "";

-- Holder (read back to decide the refusal; never decided by a preceding read)
DEFINE FIELD IF NOT EXISTS user_id ON TABLE person_operation_claim TYPE option<string>;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE person_operation_claim TYPE option<record<user>>;

-- Claim lifecycle
DEFINE FIELD IF NOT EXISTS claimed_at ON TABLE person_operation_claim TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS released_at ON TABLE person_operation_claim TYPE option<datetime>;

-- Decision carried by the claim (D39: the claim IS the vote of record)
DEFINE FIELD IF NOT EXISTS decision_option ON TABLE person_operation_claim TYPE option<string>;
DEFINE FIELD IF NOT EXISTS decided_at ON TABLE person_operation_claim TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS decision_closes_at ON TABLE person_operation_claim TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS governance_proposal_id ON TABLE person_operation_claim TYPE option<string>;
DEFINE FIELD IF NOT EXISTS governance_definition_hash ON TABLE person_operation_claim TYPE option<string>;
DEFINE FIELD IF NOT EXISTS governance_decision_hash ON TABLE person_operation_claim TYPE option<string>;
DEFINE FIELD IF NOT EXISTS governance_eligibility_json ON TABLE person_operation_claim TYPE option<string>;

-- Timestamps
DEFINE FIELD IF NOT EXISTS created_date ON TABLE person_operation_claim TYPE datetime
    DEFAULT time::now()
    READONLY;
DEFINE FIELD IF NOT EXISTS modified_date ON TABLE person_operation_claim TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS version ON TABLE person_operation_claim TYPE option<int>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS person_operation_claim_unique
    ON TABLE person_operation_claim
    FIELDS uniqueness_scope, operation_key, person_key
    UNIQUE;

DEFINE INDEX IF NOT EXISTS person_operation_claim_operation
    ON TABLE person_operation_claim
    FIELDS uniqueness_scope, operation_key;
