-- ============================================================
-- Table: consent_lifecycle_transition
-- Aggregate: Immutable consent transition evidence
-- ============================================================

DEFINE TABLE IF NOT EXISTS consent_lifecycle_transition SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE consent_lifecycle_transition TYPE string;
DEFINE FIELD IF NOT EXISTS grant_id ON TABLE consent_lifecycle_transition TYPE string;
DEFINE FIELD IF NOT EXISTS kind ON TABLE consent_lifecycle_transition TYPE string;
DEFINE FIELD IF NOT EXISTS grant_receipt_json ON TABLE consent_lifecycle_transition TYPE string;
DEFINE FIELD IF NOT EXISTS authority_json ON TABLE consent_lifecycle_transition TYPE string;
DEFINE FIELD IF NOT EXISTS event_id ON TABLE consent_lifecycle_transition TYPE string;
DEFINE FIELD IF NOT EXISTS body_hash ON TABLE consent_lifecycle_transition TYPE string;
DEFINE FIELD IF NOT EXISTS registration_id ON TABLE consent_lifecycle_transition TYPE option<string>;
DEFINE FIELD IF NOT EXISTS destination_revision ON TABLE consent_lifecycle_transition TYPE option<int>;
DEFINE FIELD IF NOT EXISTS destination_url ON TABLE consent_lifecycle_transition TYPE option<string>;
DEFINE FIELD IF NOT EXISTS occurred_at ON TABLE consent_lifecycle_transition TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS consent_transition_grant
    ON TABLE consent_lifecycle_transition
    FIELDS grant_id;
