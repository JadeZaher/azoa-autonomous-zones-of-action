-- ============================================================
-- Table: data_migration
-- Aggregate: IBackfill (Services/Backfill/IBackfill.cs)
-- Guardrail: G6 SCHEMAFULL, unique index on backfill id (applied-once ledger)
-- Note: Application-level DATA backfill ledger -- distinct from surrealforge's schema_migration DDL ledger. Records which IBackfill units have been applied so re-running the runner is a no-op. Insert-wins: the runner records a row only after a backfill's ApplyAsync succeeds; a present row means 'already applied, skip'.
-- Note: Greenfield pre-launch: zero rows to migrate today. This table + the IBackfill runner exist so no data-rewrite PATH is left unbuilt; the first consumer (Phase C/F6 FK string -> record<table> rewrite) is intentionally NOT written here.
-- ============================================================

DEFINE TABLE IF NOT EXISTS data_migration SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE data_migration TYPE string
    ASSERT $value != NONE AND $value != "";

-- Stable backfill identifier (IBackfill.Id); the applied-once dedup key
DEFINE FIELD IF NOT EXISTS backfill_id ON TABLE data_migration TYPE string
    ASSERT $value != NONE AND $value != "";

-- Human-readable backfill name at apply time (IBackfill.Name)
DEFINE FIELD IF NOT EXISTS name ON TABLE data_migration TYPE string
    ASSERT $value != NONE AND $value != "";

-- Content checksum of the backfill unit -- lets a changed body be detected (advisory; ledger keys on backfill_id)
DEFINE FIELD IF NOT EXISTS checksum ON TABLE data_migration TYPE option<string>;

-- Rows affected as reported by ApplyAsync (audit / observability)
DEFINE FIELD IF NOT EXISTS rows_affected ON TABLE data_migration TYPE int;

-- Timestamp the backfill was recorded applied (immutable after insert)
DEFINE FIELD IF NOT EXISTS applied_at ON TABLE data_migration TYPE datetime
    READONLY;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS data_migration_backfill_id_unique
    ON TABLE data_migration
    FIELDS backfill_id
    UNIQUE;
