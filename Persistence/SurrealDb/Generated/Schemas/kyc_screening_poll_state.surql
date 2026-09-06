-- ============================================================
-- Table: kyc_screening_poll_state
-- Aggregate: KycIdentity (node-operator-only person<->user binding)
-- Guardrail: G6 SCHEMAFULL
-- Note: Singleton local policy row keyed 'local', mirroring NodeGovernanceParameters. Holds the ONE date high-water mark for the batched monitoring poll: the whole point of the batched design is a single paginated list call per cycle rather than one call per monitored identity, which would collide with the shared per-IP rate ceiling.
-- ============================================================

DEFINE TABLE IF NOT EXISTS kyc_screening_poll_state SCHEMAFULL;

-- Singleton id; always 'local'
DEFINE FIELD IF NOT EXISTS id ON TABLE kyc_screening_poll_state TYPE string
    ASSERT $value != NONE AND $value != "";

-- Cold-poll high-water mark
DEFINE FIELD IF NOT EXISTS high_water_mark_date ON TABLE kyc_screening_poll_state TYPE option<string>;
DEFINE FIELD IF NOT EXISTS last_poll_started_at ON TABLE kyc_screening_poll_state TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS last_poll_completed_at ON TABLE kyc_screening_poll_state TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS last_poll_changed_count ON TABLE kyc_screening_poll_state TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS consecutive_failures ON TABLE kyc_screening_poll_state TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS backoff_until ON TABLE kyc_screening_poll_state TYPE option<datetime>;

-- Timestamps
DEFINE FIELD IF NOT EXISTS created_date ON TABLE kyc_screening_poll_state TYPE datetime
    DEFAULT time::now()
    READONLY;
DEFINE FIELD IF NOT EXISTS modified_date ON TABLE kyc_screening_poll_state TYPE option<datetime>;
