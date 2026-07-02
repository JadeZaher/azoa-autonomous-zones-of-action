namespace AZOA.WebAPI.Models.Quest;

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
/// Maps 1:1 to existing AZOA manager methods.
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
    ComposeOutputs,

    // Holon-transformation nodes (economic-primitive-nodes track).
    // Tier-1 (chain-free): GateCheck supersedes the no-op Condition; Emit hands settlement to the tenant.
    GateCheck,
    Emit,
    // Tier-2 (RequiresChainCapability == true): wrap real managers; actor from run context.
    Swap,
    Grant,
    Transfer,
    Refund,
    // fungible-token-node track: launch a fungible ASA (real total/decimals) via
    // FungibleTokenManager → IAlgorandASAModule.CreateASAAsync. RequiresChainCapability.
    FungibleTokenCreate
}

/// <summary>
/// Execution state of a per-(run, node) execution row (<see cref="QuestNodeExecution"/>).
/// Lives on <see cref="QuestNodeExecution.State"/>; the duplicate <see cref="QuestNode.State"/>
/// definition-side field is being retired by the quest-temporal-fork-model track.
/// </summary>
/// <remarks>
/// Preserve order: existing rows persisted as <c>(int)QuestNodeState</c> rely on it.
/// <c>Cancelled</c> was added by quest-temporal-fork-model for the
/// fork-cancels-parent-in-flight-nodes semantic (see ADR §2.3).
/// </remarks>
public enum QuestNodeState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
    Cancelled
}

/// <summary>
/// Type of control-flow edge between nodes.
/// </summary>
public enum QuestEdgeType
{
    Control,
    Conditional,
    /// <summary>
    /// Failure arm: target runs when the source node Failed; skipped when
    /// source Succeeded (inverse of Control). See Managers/AGENTS.md §onfailure-semantics.
    /// </summary>
    OnFailure
}

/// <summary>
/// Type of cross-quest dependency.
/// </summary>
public enum QuestDependencyType
{
    Required,
    Optional
}
