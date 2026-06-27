-- ============================================================
-- Table: quest_node_template
-- Aggregate: QuestNodeTemplate (Models/Quest/QuestNodeTemplate.cs)
-- Guardrail: G6 SCHEMAFULL; read-only catalog of reusable node definitions
-- Note: Reusable node definition shared across QuestTemplates. A QuestTemplate's embedded node slot (template_node) references a QuestNodeTemplate by id and supplies ParamOverrides; QuestInstantiator merges DefaultConfig + ParamOverrides + caller parameters into the runtime Quest's QuestNode.Config.
-- Note: node_type is the QuestNodeType enum serialised as its C# name (e.g. "HolonCreate", "WalletGet"). Asserted INSIDE the full enum value set so a schema-drift typo is rejected at INSERT time rather than at the dispatch layer.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- QuestNodeTemplate.NodeTypeKind
DEFINE PARAM IF NOT EXISTS $quest_node_template_node_type VALUE ["HolonCreate", "HolonUpdate", "HolonDelete", "HolonGet", "HolonQuery", "HolonInteract", "HolonGetChildren", "HolonGetPeers", "HolonGetAncestors", "HolonGetDescendants", "HolonPropagate", "HolonCompose", "HolonClone", "HolonMoveSubtree", "NftMint", "NftTransfer", "NftBurn", "NftGet", "NftQuery", "NftGetMetadata", "WalletCreate", "WalletUpdate", "WalletDelete", "WalletGet", "WalletQuery", "WalletSetDefault", "WalletGetPortfolio", "StarGenerate", "StarDeploy", "Search", "AvatarNFTGetComposite", "BlockchainExecute", "Condition", "ComposeOutputs", "GateCheck", "Emit", "Swap", "Grant", "Transfer", "Refund", "FungibleTokenCreate"];

DEFINE TABLE IF NOT EXISTS quest_node_template SCHEMAFULL;

-- Core identity (record id is the Guid('N') of QuestNodeTemplate.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE quest_node_template TYPE string
    ASSERT $value != NONE AND $value != "";

-- Caller-supplied label
DEFINE FIELD IF NOT EXISTS name ON TABLE quest_node_template TYPE string;

-- QuestNodeType enum name (e.g. HolonCreate, NftMint)
DEFINE FIELD IF NOT EXISTS node_type ON TABLE quest_node_template TYPE string
    ASSERT $value INSIDE $quest_node_template_node_type;

-- Optional description
DEFINE FIELD IF NOT EXISTS description ON TABLE quest_node_template TYPE option<string>;

-- JSON default config blob (string-encoded)
DEFINE FIELD IF NOT EXISTS default_config ON TABLE quest_node_template TYPE string;

-- JSON-Schema for config validation
DEFINE FIELD IF NOT EXISTS config_schema ON TABLE quest_node_template TYPE string;

-- JSON-Schema for upstream-input contract
DEFINE FIELD IF NOT EXISTS input_schema ON TABLE quest_node_template TYPE string;

-- JSON-Schema for produced-output contract
DEFINE FIELD IF NOT EXISTS output_schema ON TABLE quest_node_template TYPE string;

-- Semantic version (free string)
DEFINE FIELD IF NOT EXISTS version ON TABLE quest_node_template TYPE string;

-- Owner avatar (Guid('N') hex)
DEFINE FIELD IF NOT EXISTS author_avatar_id ON TABLE quest_node_template TYPE string
    ASSERT $value != NONE AND $value != "";

-- Marketplace visibility flag
DEFINE FIELD IF NOT EXISTS is_public ON TABLE quest_node_template TYPE bool
    DEFAULT false;

-- Free-form tags
DEFINE FIELD IF NOT EXISTS tags ON TABLE quest_node_template TYPE array<string>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS quest_node_template_by_author
    ON TABLE quest_node_template
    FIELDS author_avatar_id;

DEFINE INDEX IF NOT EXISTS quest_node_template_by_type
    ON TABLE quest_node_template
    FIELDS node_type;
