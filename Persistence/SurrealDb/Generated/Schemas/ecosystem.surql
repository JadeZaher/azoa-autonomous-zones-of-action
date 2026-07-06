-- ============================================================
-- Table: ecosystem
-- Aggregate: Ecosystem (star-odk-ecosystem-tree / final-hardening-cutover D2 -- new entity)
-- Guardrail: G6 SCHEMAFULL; greenfield entity. An Ecosystem is the ROOT of a tree of EcosystemNodes; each node references a DappSeries or a STARODK. Owned by a STARODK + avatar.
-- Note: An Ecosystem is a tree whose nodes (ecosystem_node table) each attach a DappSeries (or another STARODK) as a composable dApp. The tree is walked by ISTARManager.GetEcosystemAsync/codegen to produce the composed multi-dApp GeneratedCode on the owning STARODK. Cycle safety is enforced app-side (mirrors the holon parent-cycle guard) -- SurrealDB does not enforce acyclicity.
-- Note: star_odk_id is the owning STARODK; avatar_id is the authoritative owner (IDOR-scoped, never trusts caller-supplied owner ids). A STARODK owns at most one ecosystem in practice but the schema does not enforce that.
-- ============================================================

DEFINE TABLE IF NOT EXISTS ecosystem SCHEMAFULL;

-- Core identity (record id is the Guid('N') of Ecosystem.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE ecosystem TYPE string
    ASSERT $value != NONE AND $value != "";

-- Caller-supplied ecosystem name
DEFINE FIELD IF NOT EXISTS name ON TABLE ecosystem TYPE string
    ASSERT $value != NONE AND $value != "";

-- Optional description
DEFINE FIELD IF NOT EXISTS description ON TABLE ecosystem TYPE option<string>;

-- Owning STARODK
DEFINE FIELD IF NOT EXISTS star_odk_id ON TABLE ecosystem TYPE record<star_odk>;

-- Owner avatar (Guid('N') hex) -- authoritative, IDOR-scoped
DEFINE FIELD IF NOT EXISTS avatar_id ON TABLE ecosystem TYPE record<avatar>;

-- Deployment target chain (e.g. algorand-mainnet) -- nullable until set
DEFINE FIELD IF NOT EXISTS target_chain ON TABLE ecosystem TYPE option<string>;

-- Creation timestamp
DEFINE FIELD IF NOT EXISTS created_date ON TABLE ecosystem TYPE datetime
    READONLY;

-- Last-modified timestamp
DEFINE FIELD IF NOT EXISTS modified_date ON TABLE ecosystem TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS ecosystem_by_avatar
    ON TABLE ecosystem
    FIELDS avatar_id;

DEFINE INDEX IF NOT EXISTS ecosystem_by_star_odk
    ON TABLE ecosystem
    FIELDS star_odk_id;
