namespace AZOA.WebAPI.Models.Actions;

/// <summary>
/// Lifecycle status of an action.
/// </summary>
public enum ActionStatus
{
    Draft,
    Active,
    Completed,
    Failed,
    Archived
}

/// <summary>
/// Type of operation an ActionNode dispatches to.
/// Maps 1:1 to existing AZOA manager methods.
/// </summary>
public enum ActionNodeType
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

    // User NFT
    UserNFTGetComposite,

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
    FungibleTokenCreate,
    // Fractionalization rails (final-hardening D1). Both route through the real
    // Phase-B ICrossChainBridgeService (Algorand real, Solana fail-closed); actor
    // from run context; idempotency seeded {runId}:{nodeId}. No value/peg math in
    // the node — peg stays tenant-side (Emit). See Services/Actions/AGENTS.md §fractionalization.
    Bridge, // lock/bridge an asset cross-chain (InitiateBridgeAsync)
    Back    // reverse/release the bridged asset back (ReverseBridgeAsync)
}

/// <summary>
/// Execution state of a per-(run, node) execution row (<see cref="ActionNodeExecution"/>).
/// Lives on <see cref="ActionNodeExecution.State"/>; the duplicate <see cref="ActionNode.State"/>
/// definition-side field is being retired by the action-temporal-fork-model track.
/// </summary>
/// <remarks>
/// Preserve order: existing rows persisted as <c>(int)ActionNodeState</c> rely on it.
/// <c>Cancelled</c> was added by action-temporal-fork-model for the
/// fork-cancels-parent-in-flight-nodes semantic (see ADR §2.3).
/// </remarks>
public enum ActionNodeState
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
public enum ActionEdgeType
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
/// Type of cross-action dependency.
/// </summary>
public enum ActionDependencyType
{
    Required,
    Optional
}

/// <summary>Run-authorization dimension, orthogonal to ActionDefinition.IsPublic (discoverability). See Persistence/SurrealDb/Models/AGENTS.md §action-access-request.</summary>
public enum ActionRunAccess
{
    /// <summary>Anyone who can view the action may run/fork it (today's behavior).</summary>
    Open,
    /// <summary>Only the owner + ActionDefinition.InvitedUserIds may run/fork it.</summary>
    InviteOnly
}

/// <summary>Lifecycle of an ActionAccessRequest. Approved/Rejected/Withdrawn are terminal + immutable. See Persistence/SurrealDb/Models/AGENTS.md §action-access-request.</summary>
public enum ActionAccessRequestStatus
{
    Pending,
    Approved,
    Rejected,
    Withdrawn
}
