namespace AZOA.WebAPI.Models.Actions;

/// <summary>
/// Per-invocation context handed to an <c>IActionNodeHandler</c>. Replaces the
/// previous <c>(ActionDefinition, ActionNode)</c> pair so handlers can no longer mutate the
/// action <i>definition</i> — runtime state is now owned by <see cref="ActionRun"/>
/// and <see cref="ActionNodeExecution"/> keyed by <see cref="RunId"/>+<see cref="NodeId"/>.
/// </summary>
/// <remarks>
/// <para>
/// The context carries:
/// </para>
/// <list type="bullet">
///   <item><see cref="RunId"/> — owning <see cref="ActionRun"/>.</item>
///   <item><see cref="NodeId"/> — definition node id being executed.</item>
///   <item><see cref="ActionDefinition"/> — the immutable action definition (read-only access; handlers MUST NOT mutate).</item>
///   <item><see cref="Node"/> — convenience accessor to the matching <see cref="ActionNode"/> inside <see cref="ActionDefinition"/>.</item>
///   <item><see cref="UpstreamExecutions"/> — already-completed executions of upstream nodes, keyed by source node id. Used by <c>ComposeOutputs</c> to gather predecessor outputs without touching the definition graph.</item>
/// </list>
/// <para>
/// Pre-populating <see cref="UpstreamExecutions"/> in the manager keeps handlers
/// store-free: handlers do not depend on <c>IActionNodeExecutionStore</c>, which
/// preserves the per-aggregate seam established by the
/// <c>architecture-decoupling</c> track.
/// </para>
/// </remarks>
public sealed class ActionNodeExecutionContext
{
    public ActionNodeExecutionContext(
        Guid runId,
        Guid nodeId,
        ActionDefinition action,
        Guid actingUserId,
        IReadOnlyDictionary<Guid, ActionNodeExecution>? upstreamExecutions = null,
        Guid? actingTenantId = null,
        IReadOnlyDictionary<Guid, ActionNodeExecution>? allRunExecutions = null)
    {
        RunId = runId;
        NodeId = nodeId;
        ActionDefinition = action ?? throw new ArgumentNullException(nameof(action));
        ActingUserId = actingUserId;
        UpstreamExecutions = upstreamExecutions ?? new Dictionary<Guid, ActionNodeExecution>();
        ActingTenantId = actingTenantId;
        AllRunExecutions = allRunExecutions ?? new Dictionary<Guid, ActionNodeExecution>();
        Node = action.Nodes.FirstOrDefault(n => n.Id == nodeId)
            ?? throw new ArgumentException(
                $"Node {nodeId} not found in action {action.Id}.", nameof(nodeId));
    }

    /// <summary>Owning run id.</summary>
    public Guid RunId { get; }

    /// <summary>Definition node id being executed.</summary>
    public Guid NodeId { get; }

    /// <summary>ActionDefinition definition. Handlers MUST treat as read-only.</summary>
    public ActionDefinition ActionDefinition { get; }

    /// <summary>The definition node matching <see cref="NodeId"/>.</summary>
    public ActionNode Node { get; }

    /// <summary>
    /// The acting user — the runner. May differ from <see cref="ActionDefinition"/>.UserId
    /// for marketplace runs (a non-owner running another user's PUBLIC action).
    /// ALL identity side-effects (whose wallet/key/holon/asset a node touches) run
    /// under this user, NOT the action owner. See Services/ActionDefinition/AGENTS.md
    /// §acting-user (C1 authorization-inversion fix).
    /// </summary>
    public Guid ActingUserId { get; }

    /// <summary>
    /// Upstream node executions already produced by this run, keyed by source
    /// node id. Empty when no upstream nodes have completed (e.g. entry nodes).
    /// </summary>
    public IReadOnlyDictionary<Guid, ActionNodeExecution> UpstreamExecutions { get; }

    /// <summary>
    /// EVERY execution produced by this run so far, keyed by source node id — the
    /// scope for the run-scoped <c>run.&lt;nodeName&gt;</c> root (any prior node's
    /// output by name, not just direct-edge predecessors). Same raw material the
    /// binding resolver builds <c>run.</c> scope from, so gate predicates and $from
    /// bindings resolve the identical root. Empty for entry nodes. See
    /// Services/ActionDefinition/AGENTS.md §gate-predicate.
    /// </summary>
    public IReadOnlyDictionary<Guid, ActionNodeExecution> AllRunExecutions { get; }

    /// <summary>
    /// The tenant that drove the owning <see cref="ActionRun"/> via a tenant-driven
    /// child credential, or null when the run is user-driven
    /// (tenant-consent-delegation AC4/AC4b). Read from <c>ActionRun.ActingTenantId</c>
    /// at dispatch so it survives the async saga-worker hop. Tier-2 economic node
    /// handlers (Grant / Transfer / Refund / FungibleTokenCreate) pass it to the
    /// manager so the produced <c>BlockchainOperation</c> (or platform ASA create)
    /// carries it to the custody signing seam's live consent gate.
    /// </summary>
    public Guid? ActingTenantId { get; }
}

/// <summary>
/// Outcome of <c>IActionNodeHandler.HandleAsync</c>. Decouples handler return
/// shape from the in-place <see cref="ActionNode"/> mutation that the previous
/// <c>AZOAResult&lt;ActionNode&gt;</c> contract implied. The manager translates
/// this into a <see cref="ActionNodeExecution"/> state transition.
/// </summary>
public sealed class ActionNodeHandlerResult
{
    public bool IsError { get; init; }

    /// <summary>Free-form message. On failure this becomes <see cref="ActionNodeExecution.Error"/>.</summary>
    public string? Message { get; init; }

    /// <summary>Serialized output (JSON). On success this becomes <see cref="ActionNodeExecution.Output"/>.</summary>
    public string? Output { get; init; }

    /// <summary>The broadcast tx hash, when this node put a tx on-chain. Lets the engine reconcile against chain truth before retrying (blockchain-recovery-and-portable-wallets §1.3).</summary>
    public string? TxHash { get; init; }

    /// <summary>The chain the tx was broadcast to (e.g. "Algorand"), for provider resolution during reconciliation.</summary>
    public string? ChainType { get; init; }

    /// <summary>False for an invalid-config failure that can never succeed on retry (nothing was broadcast). The engine fails such a node terminally without a chain probe or retry budget. Default true.</summary>
    public bool Retriable { get; init; } = true;

    public static ActionNodeHandlerResult Ok(string? output, string? message = null, string? txHash = null, string? chainType = null) =>
        new() { IsError = false, Output = output, Message = message, TxHash = txHash, ChainType = chainType };

    /// <summary>
    /// Failure result. The optional <paramref name="txHash"/>/<paramref name="chainType"/>
    /// carry the broadcast tx the handler put on-chain BEFORE it failed (e.g. the
    /// confirmation read timed out): a chain-action handler MUST forward them so the
    /// reconcile-before-retry engine can verify chain truth and avoid a double-mint,
    /// instead of discarding the hash and blind-retrying
    /// (blockchain-recovery-and-portable-wallets §1.3). A non-chain failure passes
    /// neither (both null) and behaves exactly as before.
    /// </summary>
    public static ActionNodeHandlerResult Fail(string message, string? txHash = null, string? chainType = null) =>
        new() { IsError = true, Message = message, TxHash = txHash, ChainType = chainType };

    /// <summary>
    /// Invalid-config failure: nothing was broadcast and re-running can never
    /// succeed. <see cref="Retriable"/> is false so the engine fails the node
    /// terminally without a chain probe or retry budget
    /// (blockchain-recovery-and-portable-wallets §1 invalid-mode handling).
    /// </summary>
    public static ActionNodeHandlerResult Invalid(string message) =>
        new() { IsError = true, Message = message, Retriable = false };
}
