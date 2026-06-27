-- ============================================================
-- Table: quest_node
-- Aggregate: QuestNode (Models/Quest/QuestNode.cs)
-- Guardrail: G6 SCHEMAFULL; definition-side step inside a quest DAG. Per-(run,node) runtime state lives on quest_node_execution.
-- Note: Each row is one step inside a quest DAG. config is a JSON-serialized request model (e.g. HolonCreateModel, NftMintRequest) that the matching IQuestNodeHandler deserializes at execution time. node_type asserts INSIDE the full QuestNodeType enum so a schema-drift typo is rejected at INSERT time rather than at the dispatch layer.
-- Note: is_entry/is_terminal are derived from the edge graph (an entry node has no incoming control edges) and cached on the row for fast quest-start lookup. execution_order is the topological position computed by QuestDagValidator on activation; the (quest_id, execution_order) composite index is used by the executor to walk nodes in dependency order.
-- Note: state/output/error were intentionally removed when quest-temporal-fork-model split per-run state into quest_node_execution -- see SURREAL-SCHEMA-HINTS.md §2 'Removed from quest_node'.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- QuestNode.QuestNodeTypeKind
DEFINE PARAM IF NOT EXISTS $quest_node_node_type VALUE ["HolonCreate", "HolonUpdate", "HolonDelete", "HolonGet", "HolonQuery", "HolonInteract", "HolonGetChildren", "HolonGetPeers", "HolonGetAncestors", "HolonGetDescendants", "HolonPropagate", "HolonCompose", "HolonClone", "HolonMoveSubtree", "NftMint", "NftTransfer", "NftBurn", "NftGet", "NftQuery", "NftGetMetadata", "WalletCreate", "WalletUpdate", "WalletDelete", "WalletGet", "WalletQuery", "WalletSetDefault", "WalletGetPortfolio", "StarGenerate", "StarDeploy", "Search", "AvatarNFTGetComposite", "BlockchainExecute", "Condition", "ComposeOutputs", "GateCheck", "Emit", "Swap", "Grant", "Transfer", "Refund", "FungibleTokenCreate"];

DEFINE TABLE IF NOT EXISTS quest_node SCHEMAFULL;

-- Core identity (record id is the Guid('N') of QuestNode.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE quest_node TYPE string
    ASSERT $value != NONE AND $value != "";

-- Owning quest
DEFINE FIELD IF NOT EXISTS quest_id ON TABLE quest_node TYPE record<quest>;

-- Reusable node template this node was instantiated from (null for hand-authored)
DEFINE FIELD IF NOT EXISTS node_template_id ON TABLE quest_node TYPE option<record<quest_node_template>>;

-- QuestNodeType enum name (e.g. HolonCreate, NftMint)
DEFINE FIELD IF NOT EXISTS node_type ON TABLE quest_node TYPE string
    ASSERT $value INSIDE $quest_node_node_type;

-- Caller-supplied label
DEFINE FIELD IF NOT EXISTS name ON TABLE quest_node TYPE string;

-- Node-specific JSON config -- deserialized to the matching request model at execution time
DEFINE FIELD IF NOT EXISTS config ON TABLE quest_node TYPE string
    DEFAULT "{}";

-- Entry point (no incoming control edges) -- cached from DAG analysis
DEFINE FIELD IF NOT EXISTS is_entry ON TABLE quest_node TYPE bool
    DEFAULT false;

-- Terminal node (no outgoing control edges) -- cached from DAG analysis
DEFINE FIELD IF NOT EXISTS is_terminal ON TABLE quest_node TYPE bool
    DEFAULT false;

-- Topological position (0-based; computed during DAG validation)
DEFINE FIELD IF NOT EXISTS execution_order ON TABLE quest_node TYPE int;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS quest_node_by_quest
    ON TABLE quest_node
    FIELDS quest_id;

DEFINE INDEX IF NOT EXISTS quest_node_by_order
    ON TABLE quest_node
    FIELDS quest_id, execution_order;
