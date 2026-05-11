namespace OASIS.WebAPI.Models.Quest;

/// <summary>
/// Lifecycle status of a Quest.
/// </summary>
public enum QuestStatus
{
    Draft,
    Active,
    Completed,
    Failed,
    Archived
}

/// <summary>
/// Type of operation a QuestNode dispatches to.
/// Maps 1:1 to existing OASIS manager methods.
/// </summary>
public enum QuestNodeType
{
    // Holon operations
    HolonCreate,
    HolonUpdate,
    HolonDelete,
    HolonGet,
    HolonQuery,
    HolonInteract,
    HolonGetChildren,
    HolonGetPeers,
    HolonGetAncestors,
    HolonGetDescendants,
    HolonPropagate,
    HolonCompose,
    HolonClone,
    HolonMoveSubtree,

    // NFT operations
    NftMint,
    NftTransfer,
    NftBurn,
    NftGet,
    NftQuery,
    NftGetMetadata,

    // Wallet operations
    WalletCreate,
    WalletUpdate,
    WalletDelete,
    WalletGet,
    WalletQuery,
    WalletSetDefault,
    WalletGetPortfolio,

    // STAR operations
    StarGenerate,
    StarDeploy,

    // Search
    Search,

    // Avatar NFT
    AvatarNFTGetComposite,

    // Blockchain
    BlockchainExecute,

    // Internal/control-flow
    Condition,
    ComposeOutputs
}

/// <summary>
/// Execution state of a QuestNode.
/// </summary>
public enum QuestNodeState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped
}

/// <summary>
/// Type of control-flow edge between nodes.
/// </summary>
public enum QuestEdgeType
{
    Control,
    Conditional
}

/// <summary>
/// Type of cross-quest dependency.
/// </summary>
public enum QuestDependencyType
{
    Required,
    Optional
}
