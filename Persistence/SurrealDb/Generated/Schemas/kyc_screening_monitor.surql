-- ============================================================
-- Table: kyc_screening_monitor
-- Aggregate: KycIdentity (node-operator-only person<->user binding)
-- Guardrail: G6 SCHEMAFULL
-- Note: NODE-OPERATOR-ONLY. Ongoing AML monitoring has NO push channel of any kind — the provider's callback type list contains no screening event — so this table is driven by a cold batched poll with a date high-water mark.
-- Note: The provider keys a monitor on the QUERY SHAPE (fullName + birthdayTimeFrom), not on a resolved person id, so the exact registered query fields are persisted here. Re-registering the same shape returns argumentError 'already-exists', which is benign idempotent success, not a failure.
-- Note: Uniqueness is (tenant_scope, query_full_name, query_birthday_time_from), mirroring kyc_identity's (tenant_scope, person_key). A monitor key WITHOUT a tenant component would let tenant B's registration resolve to tenant A's row — leaking A's subject user and tenant id — and would make the bare existence of another tenant's monitored person observable as a unique-index violation.
-- Note: kyc_screening_monitor_provider is deliberately NOT unique. One vendor company key serves the whole node, so the vendor dedups a query shape globally and ONE upstream monitor legitimately backs one local row per tenant. A unique index there would refuse the second tenant's registration, which is the same cross-tenant refusal D23 rejects for person_key.
-- Note: B3 nullable-collision review: query_full_name and query_birthday_time_from are option<string> so erasure can tombstone them (D25), and both sit in the UNIQUE index. Verified empirically on SurrealDB v3.1.4: rows whose indexed tuple differs only by a NONE component do not collide, so erased monitors never contend with each other or with live ones. A live registration always writes a non-NONE name and a non-NONE (possibly empty) birthday, so dedup is correct by construction; NONE means erased, "" means registered without a birthday.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- KycScreeningMonitor.KycMonitorState
DEFINE PARAM IF NOT EXISTS $kyc_screening_monitor_state VALUE ["ACTIVE", "REMOVED"];

DEFINE TABLE IF NOT EXISTS kyc_screening_monitor SCHEMAFULL;

-- Core identity (record id is the Guid('N') of the registration)
DEFINE FIELD IF NOT EXISTS id ON TABLE kyc_screening_monitor TYPE string
    ASSERT $value != NONE AND $value != "";

-- Provider correlation
DEFINE FIELD IF NOT EXISTS provider_monitor_id ON TABLE kyc_screening_monitor TYPE string
    ASSERT $value != NONE AND $value != "";

-- Subject + tenancy (tenant_scope is the uniqueness domain)
DEFINE FIELD IF NOT EXISTS tenant_scope ON TABLE kyc_screening_monitor TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS subject_user_id ON TABLE kyc_screening_monitor TYPE option<record<user>>;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE kyc_screening_monitor TYPE option<record<user>>;

-- Exact registered query shape (the provider's monitor key)
DEFINE FIELD IF NOT EXISTS query_full_name ON TABLE kyc_screening_monitor TYPE option<string>;
DEFINE FIELD IF NOT EXISTS query_birthday_time_from ON TABLE kyc_screening_monitor TYPE option<string>;
DEFINE FIELD IF NOT EXISTS follow_up_score_threshold ON TABLE kyc_screening_monitor TYPE int
    DEFAULT 0;

-- Lifecycle
DEFINE FIELD IF NOT EXISTS state ON TABLE kyc_screening_monitor TYPE string
    ASSERT $value INSIDE $kyc_screening_monitor_state;
DEFINE FIELD IF NOT EXISTS registered_idempotently ON TABLE kyc_screening_monitor TYPE bool;

-- Cold-poll high-water mark
DEFINE FIELD IF NOT EXISTS last_observed_update_date ON TABLE kyc_screening_monitor TYPE option<string>;
DEFINE FIELD IF NOT EXISTS last_polled_at ON TABLE kyc_screening_monitor TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS last_follow_up_at ON TABLE kyc_screening_monitor TYPE option<datetime>;

-- Retention + erasure (the query shape is name + date of birth)
DEFINE FIELD IF NOT EXISTS retention_expires_at ON TABLE kyc_screening_monitor TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS erased_at ON TABLE kyc_screening_monitor TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS erasure_reason ON TABLE kyc_screening_monitor TYPE option<string>;

-- Timestamps
DEFINE FIELD IF NOT EXISTS registered_at ON TABLE kyc_screening_monitor TYPE datetime
    READONLY;
DEFINE FIELD IF NOT EXISTS created_date ON TABLE kyc_screening_monitor TYPE datetime;
DEFINE FIELD IF NOT EXISTS modified_date ON TABLE kyc_screening_monitor TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS removed_at ON TABLE kyc_screening_monitor TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS kyc_screening_monitor_query
    ON TABLE kyc_screening_monitor
    FIELDS tenant_scope, query_full_name, query_birthday_time_from
    UNIQUE;

DEFINE INDEX IF NOT EXISTS kyc_screening_monitor_provider
    ON TABLE kyc_screening_monitor
    FIELDS provider_monitor_id;

DEFINE INDEX IF NOT EXISTS kyc_screening_monitor_state
    ON TABLE kyc_screening_monitor
    FIELDS state;

DEFINE INDEX IF NOT EXISTS kyc_screening_monitor_retention
    ON TABLE kyc_screening_monitor
    FIELDS retention_expires_at;
