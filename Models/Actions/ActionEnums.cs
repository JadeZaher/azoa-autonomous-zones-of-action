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

    // Action graph bundle operations
    ActionGraphBundleGenerate,
    ActionGraphBundleDeploy,

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
    Back,   // reverse/release the bridged asset back (ReverseBridgeAsync)

    // Verifiable-credential rails (Phase 5 D19). Tier-2 (RequiresChainCapability == true):
    // revocation is published on-chain, so the run context must be chain-capable.
    CredentialRevoke,

    // Governance rails (Phase 5 D20). Tier-1 (chain-free) -- proposal, ballot and tally are
    // holon writes. GovernanceVote is the consumer of the person-uniqueness gate
    // (PersonUniquenessRequirement / KycIneligibilityCause.PersonAlreadyClaimedOperation).
    GovernanceProposalCreate,
    GovernanceVote,
    GovernanceTally,

    // Credit rails (Phase 5 D21). Tier-1 (chain-free): the loan ledger is holonic and every
    // amount is a RailAmount; settlement of that amount is handed to the tenant via Emit.
    LoanOpen,
    LoanRepaymentRecord,
    LoanBalance,

    // Hold rails (D14 / W3.4). Tier-2 (RequiresChainCapability == true): every one of
    // them either commits units in the platform-custodial holding account or moves them
    // out of it, so the run context must be able to settle on the rail it names.
    // Appended at the END deliberately -- existing rows persist the ENUM NAME (see
    // action_node.surql $action_node_node_type), and appending keeps the numeric
    // ordinals of every earlier member stable for anything that persisted them as int.
    //
    // Hold           -- park units from a named reserve partition until releaseNotBefore.
    // Release        -- hand a due hold's units to its beneficiary (the only outbound move).
    // HoldRefund     -- abandon a hold; the units go back to the partition's availability.
    // ReleaseIntoHold-- close one hold by opening the NEXT one over the same units, which
    //                   is what "approve completion" is: a release into the worker's own
    //                   window. A plain Release cannot express it because Release ends the
    //                   commitment, and the second window must keep it.
    //
    // NOTE the name HoldRefund, not Refund: `Refund` above is the NFT-transfer reversal
    // node and is a different authority over a different asset shape. Two node types
    // cannot share one name, and silently overloading the existing one would have let an
    // NFT reversal reach the hold ledger. See Services/Hold/AGENTS.md.
    Hold,
    Release,
    HoldRefund,
    ReleaseIntoHold
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
