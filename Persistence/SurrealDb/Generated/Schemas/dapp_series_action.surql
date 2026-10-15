-- ============================================================
-- Table: dapp_series_action
-- Aggregate: DappSeriesAction (dapp-composition track -- new entity, no hand-written model)
-- Guardrail: G6 SCHEMAFULL; greenfield entity introduced by the dapp-composition track. Carries the ordering and cross-action data flow within a DappSeries.
-- Note: An ordered action entry inside a DappSeries. Composite key concept: (dapp_series_id, action_id) is logically unique but stored as a real row to allow soft-update of order and input_mappings without delete+reinsert.
-- Note: order is 1-indexed and dense: ReorderActionAsync resequences sibling rows transactionally. input_mappings is a JSON array describing how outputs from prior actions' terminal nodes flow into this action's entry nodes (see DappSeries spec §InputMapping for the shape: sourceActionId/sourceNodeId/targetActionId/targetNodeId/fieldMap).
-- ============================================================

DEFINE TABLE IF NOT EXISTS dapp_series_action SCHEMAFULL;

-- Core identity (record id is the Guid('N') of DappSeriesAction.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE dapp_series_action TYPE string
    ASSERT $value != NONE AND $value != "";

-- Parent series
DEFINE FIELD IF NOT EXISTS dapp_series_id ON TABLE dapp_series_action TYPE record<dapp_series>;

-- Referenced action
DEFINE FIELD IF NOT EXISTS action_id ON TABLE dapp_series_action TYPE record<action>;

-- 1-indexed execution order within the series
DEFINE FIELD IF NOT EXISTS order ON TABLE dapp_series_action TYPE int
    ASSERT $value > 0;

-- JSON array of InputMapping entries (null when no cross-action flow needed)
DEFINE FIELD IF NOT EXISTS input_mappings ON TABLE dapp_series_action TYPE option<string>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS dapp_series_action_by_series
    ON TABLE dapp_series_action
    FIELDS dapp_series_id;

DEFINE INDEX IF NOT EXISTS dapp_series_action_by_order
    ON TABLE dapp_series_action
    FIELDS dapp_series_id, order;

DEFINE INDEX IF NOT EXISTS dapp_series_action_by_action
    ON TABLE dapp_series_action
    FIELDS action_id;
