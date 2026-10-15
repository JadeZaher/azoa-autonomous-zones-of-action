-- ============================================================
-- Table: action_node_template
-- Aggregate: ActionNodeTemplate (Models/Actions/ActionNodeTemplate.cs)
-- Guardrail: G6 SCHEMAFULL; read-only catalog of reusable node definitions
-- Note: Reusable node definition shared across ActionTemplates. An ActionTemplate's embedded node slot (template_node) references an ActionNodeTemplate by id and supplies ParamOverrides; ActionInstantiator merges DefaultConfig + ParamOverrides + caller parameters into the runtime ActionDefinition's ActionNode.Config.
-- Note: node_type is the ActionNodeType enum serialised as its C# name (e.g. "HolonCreate", "WalletGet"). Asserted INSIDE the full enum value set so a schema-drift typo is rejected at INSERT time rather than at the dispatch layer.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- ActionNodeTemplate.NodeTypeKind
DEFINE PARAM IF NOT EXISTS $action_node_template_node_type VALUE ["HolonCreate", "HolonUpdate", "HolonDelete", "HolonGet", "HolonQuery", "HolonInteract", "HolonGetChildren", "HolonGetPeers", "HolonGetAncestors", "HolonGetDescendants", "HolonPropagate", "HolonCompose", "HolonClone", "HolonMoveSubtree", "NftMint", "NftTransfer", "NftBurn", "NftGet", "NftQuery", "NftGetMetadata", "WalletCreate", "WalletUpdate", "WalletDelete", "WalletGet", "WalletQuery", "WalletSetDefault", "WalletGetPortfolio", "ActionGraphBundleGenerate", "ActionGraphBundleDeploy", "Search", "UserNFTGetComposite", "BlockchainExecute", "Condition", "ComposeOutputs", "GateCheck", "Emit", "Swap", "Grant", "Transfer", "Refund", "FungibleTokenCreate", "Bridge", "Back", "CredentialRevoke", "GovernanceProposalCreate", "GovernanceVote", "GovernanceTally", "LoanOpen", "LoanRepaymentRecord", "LoanBalance", "Hold", "Release", "HoldRefund", "ReleaseIntoHold"];

DEFINE TABLE IF NOT EXISTS action_node_template SCHEMAFULL;

-- Core identity (record id is the Guid('N') of ActionNodeTemplate.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE action_node_template TYPE string
    ASSERT $value != NONE AND $value != "";

-- Caller-supplied label
DEFINE FIELD IF NOT EXISTS name ON TABLE action_node_template TYPE string;

-- ActionNodeType enum name (e.g. HolonCreate, NftMint)
DEFINE FIELD IF NOT EXISTS node_type ON TABLE action_node_template TYPE string
    ASSERT $value INSIDE $action_node_template_node_type;

-- Optional description
DEFINE FIELD IF NOT EXISTS description ON TABLE action_node_template TYPE option<string>;

-- JSON default config blob (string-encoded)
DEFINE FIELD IF NOT EXISTS default_config ON TABLE action_node_template TYPE string;

-- JSON-Schema for config validation
DEFINE FIELD IF NOT EXISTS config_schema ON TABLE action_node_template TYPE string;

-- JSON-Schema for upstream-input contract
DEFINE FIELD IF NOT EXISTS input_schema ON TABLE action_node_template TYPE string;

-- JSON-Schema for produced-output contract
DEFINE FIELD IF NOT EXISTS output_schema ON TABLE action_node_template TYPE string;

-- Semantic version (free string)
DEFINE FIELD IF NOT EXISTS version ON TABLE action_node_template TYPE string;

-- Owner user (Guid('N') hex)
DEFINE FIELD IF NOT EXISTS author_user_id ON TABLE action_node_template TYPE string
    ASSERT $value != NONE AND $value != "";

-- Marketplace visibility flag
DEFINE FIELD IF NOT EXISTS is_public ON TABLE action_node_template TYPE bool
    DEFAULT false;

-- Free-form tags
DEFINE FIELD IF NOT EXISTS tags ON TABLE action_node_template TYPE array<string>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS action_node_template_by_author
    ON TABLE action_node_template
    FIELDS author_user_id;

DEFINE INDEX IF NOT EXISTS action_node_template_by_type
    ON TABLE action_node_template
    FIELDS node_type;
