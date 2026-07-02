-- ============================================================
-- Table: quest_edge
-- Aggregate: QuestEdge (Models/Quest/QuestEdge.cs)
-- Guardrail: G6 SCHEMAFULL; definition-side directed control-flow edge between two quest_node rows in the same quest.
-- Note: Scalar edge table is the conservative shape; the surrealdb-migration track may promote this to a native `RELATE source_node -> control_flow -> target_node` edge for cheaper traversal. Both shapes preserve intra-iteration DAG semantics; the scalar form keeps QuestDagValidator queries unchanged.
-- Note: condition is populated only when edge_type = Conditional; the ConditionNodeHandler evaluates the expression against the upstream node's serialized output to gate edge activation.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- QuestEdge.QuestEdgeTypeKind
DEFINE PARAM IF NOT EXISTS $quest_edge_edge_type VALUE ["Control", "Conditional", "OnFailure"];

DEFINE TABLE IF NOT EXISTS quest_edge SCHEMAFULL;

-- Core identity (record id is the Guid('N') of QuestEdge.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE quest_edge TYPE string
    ASSERT $value != NONE AND $value != "";

-- Owning quest
DEFINE FIELD IF NOT EXISTS quest_id ON TABLE quest_edge TYPE record<quest>;

-- Upstream node
DEFINE FIELD IF NOT EXISTS source_node_id ON TABLE quest_edge TYPE record<quest_node>;

-- Downstream node
DEFINE FIELD IF NOT EXISTS target_node_id ON TABLE quest_edge TYPE record<quest_node>;

-- Optional condition expression (only with edge_type=Conditional)
DEFINE FIELD IF NOT EXISTS condition ON TABLE quest_edge TYPE option<string>;

-- QuestEdgeType enum name
DEFINE FIELD IF NOT EXISTS edge_type ON TABLE quest_edge TYPE string
    ASSERT $value INSIDE $quest_edge_edge_type
    DEFAULT "Control";

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS quest_edge_by_quest
    ON TABLE quest_edge
    FIELDS quest_id;

DEFINE INDEX IF NOT EXISTS quest_edge_by_source
    ON TABLE quest_edge
    FIELDS source_node_id;

DEFINE INDEX IF NOT EXISTS quest_edge_by_target
    ON TABLE quest_edge
    FIELDS target_node_id;
