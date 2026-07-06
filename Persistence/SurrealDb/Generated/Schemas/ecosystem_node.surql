-- ============================================================
-- Table: ecosystem_node
-- Aggregate: EcosystemNode (star-odk-ecosystem-tree / final-hardening-cutover D2 -- new entity)
-- Guardrail: G6 SCHEMAFULL; greenfield entity. A single node in an Ecosystem tree. parent_node_id null = a root child of the ecosystem; non-null = nested under another EcosystemNode. ref_kind + ref_id name the attached DappSeries or STARODK.
-- Note: An EcosystemNode attaches ONE composable dApp (a DappSeries via ref_kind='DappSeries', or a STARODK via ref_kind='StarOdk') into the ecosystem tree. Tree edges are modelled by parent_node_id (self-reference); the ecosystem root is the Ecosystem row, so a node whose parent_node_id is null hangs directly off the ecosystem. Codegen walks this tree parent->children (see ISTARManager tree-walking codegen) with an app-side visited-set cycle guard mirroring the holon parent-cycle precedent.
-- Note: ref_id is the Guid('N') hex of the attached DappSeries.Id or STARODK.Id. It is intentionally NOT a typed record<> FK because it is a polymorphic reference (DappSeries OR star_odk) discriminated by ref_kind -- SurrealDB record<> columns are single-table. Ownership is validated app-side, not by schema FK.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- EcosystemNode.RefKindValue
DEFINE PARAM IF NOT EXISTS $ecosystem_node_ref_kind VALUE ["DappSeries", "StarOdk"];

DEFINE TABLE IF NOT EXISTS ecosystem_node SCHEMAFULL;

-- Core identity (record id is the Guid('N') of EcosystemNode.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE ecosystem_node TYPE string
    ASSERT $value != NONE AND $value != "";

-- Owning ecosystem
DEFINE FIELD IF NOT EXISTS ecosystem_id ON TABLE ecosystem_node TYPE record<ecosystem>;

-- Parent node in the tree (null = root child of the ecosystem)
DEFINE FIELD IF NOT EXISTS parent_node_id ON TABLE ecosystem_node TYPE option<record<ecosystem_node>>;

-- Discriminator for the attached reference (DappSeries or StarOdk)
DEFINE FIELD IF NOT EXISTS ref_kind ON TABLE ecosystem_node TYPE string
    ASSERT $value INSIDE $ecosystem_node_ref_kind
    DEFAULT "DappSeries";

-- Guid('N') hex of the attached DappSeries.Id or STARODK.Id (polymorphic, app-validated)
DEFINE FIELD IF NOT EXISTS ref_id ON TABLE ecosystem_node TYPE string
    ASSERT $value != NONE AND $value != "";

-- Optional display label for the node in the tree UI
DEFINE FIELD IF NOT EXISTS label ON TABLE ecosystem_node TYPE option<string>;

-- Creation timestamp
DEFINE FIELD IF NOT EXISTS created_date ON TABLE ecosystem_node TYPE datetime
    READONLY;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS ecosystem_node_by_ecosystem
    ON TABLE ecosystem_node
    FIELDS ecosystem_id;

DEFINE INDEX IF NOT EXISTS ecosystem_node_by_parent
    ON TABLE ecosystem_node
    FIELDS parent_node_id;
