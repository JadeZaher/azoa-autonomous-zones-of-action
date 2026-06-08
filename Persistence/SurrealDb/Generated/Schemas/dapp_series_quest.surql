-- ============================================================
-- Table: dapp_series_quest
-- Aggregate: DappSeriesQuest (dapp-composition track -- new entity, no hand-written model)
-- Guardrail: G6 SCHEMAFULL; greenfield entity introduced by the dapp-composition track. Carries the ordering and cross-quest data flow within a DappSeries.
-- Note: An ordered quest entry inside a DappSeries. Composite key concept: (dapp_series_id, quest_id) is logically unique but stored as a real row to allow soft-update of order and input_mappings without delete+reinsert.
-- Note: order is 1-indexed and dense: ReorderQuestAsync resequences sibling rows transactionally. input_mappings is a JSON array describing how outputs from prior quests' terminal nodes flow into this quest's entry nodes (see DappSeries spec §InputMapping for the shape: sourceQuestId/sourceNodeId/targetQuestId/targetNodeId/fieldMap).
-- ============================================================

DEFINE TABLE IF NOT EXISTS dapp_series_quest SCHEMAFULL;

-- Core identity (record id is the Guid('N') of DappSeriesQuest.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE dapp_series_quest TYPE string
    ASSERT $value != NONE AND $value != "";

-- Parent series
DEFINE FIELD IF NOT EXISTS dapp_series_id ON TABLE dapp_series_quest TYPE record<dapp_series>;

-- Referenced quest
DEFINE FIELD IF NOT EXISTS quest_id ON TABLE dapp_series_quest TYPE record<quest>;

-- 1-indexed execution order within the series
DEFINE FIELD IF NOT EXISTS order ON TABLE dapp_series_quest TYPE int
    ASSERT $value > 0;

-- JSON array of InputMapping entries (null when no cross-quest flow needed)
DEFINE FIELD IF NOT EXISTS input_mappings ON TABLE dapp_series_quest TYPE option<string>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS dapp_series_quest_by_series
    ON TABLE dapp_series_quest
    FIELDS dapp_series_id;

DEFINE INDEX IF NOT EXISTS dapp_series_quest_by_order
    ON TABLE dapp_series_quest
    FIELDS dapp_series_id, order;

DEFINE INDEX IF NOT EXISTS dapp_series_quest_by_quest
    ON TABLE dapp_series_quest
    FIELDS quest_id;
