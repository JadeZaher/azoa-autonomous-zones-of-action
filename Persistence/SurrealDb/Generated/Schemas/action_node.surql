-- ============================================================
-- Table: action_node
-- Aggregate: ActionNode (Models/Actions/ActionNode.cs)
-- Guardrail: G6 SCHEMAFULL; definition-side step inside an action DAG. Per-(run,node) runtime state lives on action_node_execution.
-- Note: Each row is one step inside an action DAG. config is a JSON-serialized request model (e.g. HolonCreateModel, NftMintRequest) that the matching IActionNodeHandler deserializes at execution time. node_type asserts INSIDE the full ActionNodeType enum so a schema-drift typo is rejected at INSERT time rather than at the dispatch layer.
-- Note: is_entry/is_terminal are derived from the edge graph (an entry node has no incoming control edges) and cached on the row for fast action-start lookup. execution_order is the topological position computed by ActionDagValidator on activation; the (action_id, execution_order) composite index is used by the executor to walk nodes in dependency order.
-- Note: state/output/error were intentionally removed when action-temporal-fork-model split per-run state into action_node_execution -- see SURREAL-SCHEMA-HINTS.md §2 'Removed from action_node'.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- ActionNode.ActionNodeTypeKind
DEFINE PARAM IF NOT EXISTS $action_node_node_type VALUE ["HolonCreate", "HolonUpdate", "HolonDelete", "HolonGet", "HolonQuery", "HolonInteract", "HolonGetChildren", "HolonGetPeers", "HolonGetAncestors", "HolonGetDescendants", "HolonPropagate", "HolonCompose", "HolonClone", "HolonMoveSubtree", "NftMint", "NftTransfer", "NftBurn", "NftGet", "NftQuery", "NftGetMetadata", "WalletCreate", "WalletUpdate", "WalletDelete", "WalletGet", "WalletQuery", "WalletSetDefault", "WalletGetPortfolio", "ActionGraphBundleGenerate", "ActionGraphBundleDeploy", "Search", "UserNFTGetComposite", "BlockchainExecute", "Condition", "ComposeOutputs", "GateCheck", "Emit", "Swap", "Grant", "Transfer", "Refund", "FungibleTokenCreate", "Bridge", "Back", "CredentialRevoke", "GovernanceProposalCreate", "GovernanceVote", "GovernanceTally", "LoanOpen", "LoanRepaymentRecord", "LoanBalance"];

DEFINE TABLE IF NOT EXISTS action_node SCHEMAFULL;

-- Core identity (record id is the Guid('N') of ActionNode.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE action_node TYPE string
    ASSERT $value != NONE AND $value != "";

-- Owning action
DEFINE FIELD IF NOT EXISTS action_id ON TABLE action_node TYPE record<action>;

-- Reusable node template this node was instantiated from (null for hand-authored)
DEFINE FIELD IF NOT EXISTS node_template_id ON TABLE action_node TYPE option<record<action_node_template>>;

-- ActionNodeType enum name (e.g. HolonCreate, NftMint)
DEFINE FIELD IF NOT EXISTS node_type ON TABLE action_node TYPE string
    ASSERT $value INSIDE $action_node_node_type;

-- Caller-supplied label
DEFINE FIELD IF NOT EXISTS name ON TABLE action_node TYPE string;

-- Node-specific JSON config -- deserialized to the matching request model at execution time
DEFINE FIELD IF NOT EXISTS config ON TABLE action_node TYPE string
    DEFAULT "{}";

-- Entry point (no incoming control edges) -- cached from DAG analysis
DEFINE FIELD IF NOT EXISTS is_entry ON TABLE action_node TYPE bool
    DEFAULT false;

-- Terminal node (no outgoing control edges) -- cached from DAG analysis
DEFINE FIELD IF NOT EXISTS is_terminal ON TABLE action_node TYPE bool
    DEFAULT false;

-- Topological position (0-based; computed during DAG validation)
DEFINE FIELD IF NOT EXISTS execution_order ON TABLE action_node TYPE int;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS action_node_by_action
    ON TABLE action_node
    FIELDS action_id;

DEFINE INDEX IF NOT EXISTS action_node_by_order
    ON TABLE action_node
    FIELDS action_id, execution_order;
