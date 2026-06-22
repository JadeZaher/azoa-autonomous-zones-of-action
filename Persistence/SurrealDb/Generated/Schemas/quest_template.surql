-- ============================================================
-- Table: quest_template
-- Aggregate: QuestTemplate (Models/Quest/QuestTemplate.cs)
-- Guardrail: G6 SCHEMAFULL; read-only catalog data populated by template authors
-- Note: Definition-side quest catalog. A QuestTemplate is a reusable DAG blueprint instantiated by QuestInstantiator (Services/Quest/QuestInstantiator.cs) into a runtime Quest with concrete parameters. The runtime quest_run + quest_node_execution tables live in [[quest-temporal-fork-model]] (see SURREAL-SCHEMA-HINTS.md); this table is the parameterised definition the instantiator reads.
-- Note: Nodes + edges embed as FLEXIBLE array<object> fields on the parent template row. They are NEVER queried independently of the template -- the instantiator reads the whole template in one record-id SELECT, walks the embedded slots, then resolves each NodeTemplateId via quest_node_template (table 140). Embedding keeps the read O(1) per template; separating into child tables would force a join that has no other consumer.
-- Note: parameters is a JSON-Schema-encoded string declaring required parameters for instantiation (e.g. {"required":["holonId"]}). version + tags + is_public are surfaced by the future template-marketplace UI.
-- ============================================================

DEFINE TABLE IF NOT EXISTS quest_template SCHEMAFULL;

-- Core identity (record id is the Guid('N') of QuestTemplate.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE quest_template TYPE string
    ASSERT $value != NONE AND $value != "";

-- Caller-supplied label
DEFINE FIELD IF NOT EXISTS name ON TABLE quest_template TYPE string;

-- Optional description
DEFINE FIELD IF NOT EXISTS description ON TABLE quest_template TYPE option<string>;

-- Owner avatar (Guid('N') hex)
DEFINE FIELD IF NOT EXISTS author_avatar_id ON TABLE quest_template TYPE string
    ASSERT $value != NONE AND $value != "";

-- JSON-Schema for instantiation parameters (string-encoded)
DEFINE FIELD IF NOT EXISTS parameters ON TABLE quest_template TYPE string;

-- Semantic version (free string -- e.g. 1.0.0)
DEFINE FIELD IF NOT EXISTS version ON TABLE quest_template TYPE string;

-- Marketplace visibility flag
DEFINE FIELD IF NOT EXISTS is_public ON TABLE quest_template TYPE bool
    DEFAULT false;

-- Embedded template-node slots (FLEXIBLE -- shape validated by C# POCO at deserialization time, not by SurrealDB).
DEFINE FIELD IF NOT EXISTS nodes ON TABLE quest_template TYPE array<object> FLEXIBLE;
DEFINE FIELD OVERWRITE nodes.* ON TABLE quest_template TYPE object FLEXIBLE;

-- Embedded template-edge slots (FLEXIBLE -- same rationale as nodes).
DEFINE FIELD IF NOT EXISTS edges ON TABLE quest_template TYPE array<object> FLEXIBLE;
DEFINE FIELD OVERWRITE edges.* ON TABLE quest_template TYPE object FLEXIBLE;

-- Free-form tags.
DEFINE FIELD IF NOT EXISTS tags ON TABLE quest_template TYPE array<string>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS quest_template_by_author
    ON TABLE quest_template
    FIELDS author_avatar_id;
