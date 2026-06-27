namespace AZOA.WebAPI.Models.Quest;

/// <summary>
/// Per-invocation context handed to an <c>IQuestNodeHandler</c>. Replaces the
/// previous <c>(Quest, QuestNode)</c> pair so handlers can no longer mutate the
/// quest <i>definition</i> — runtime state is now owned by <see cref="QuestRun"/>
/// and <see cref="QuestNodeExecution"/> keyed by <see cref="RunId"/>+<see cref="NodeId"/>.
/// </summary>
/// <remarks>
/// <para>
/// The context carries:
/// </para>
/// <list type="bullet">
///   <item><see cref="RunId"/> — owning <see cref="QuestRun"/>.</item>
///   <item><see cref="NodeId"/> — definition node id being executed.</item>
///   <item><see cref="Quest"/> — the immutable quest definition (read-only access; handlers MUST NOT mutate).</item>
///   <item><see cref="Node"/> — convenience accessor to the matching <see cref="QuestNode"/> inside <see cref="Quest"/>.</item>
///   <item><see cref="UpstreamExecutions"/> — already-completed executions of upstream nodes, keyed by source node id. Used by <c>ComposeOutputs</c> to gather predecessor outputs without touching the definition graph.</item>
/// </list>
/// <para>
/// Pre-populating <see cref="UpstreamExecutions"/> in the manager keeps handlers
/// store-free: handlers do not depend on <c>IQuestNodeExecutionStore</c>, which
/// preserves the per-aggregate seam established by the
/// <c>architecture-decoupling</c> track.
/// </para>
/// </remarks>
public sealed class QuestNodeExecutionContext
{
    public QuestNodeExecutionContext(
        Guid runId,
        Guid nodeId,
        Quest quest,
        IReadOnlyDictionary<Guid, QuestNodeExecution>? upstreamExecutions = null,
        Guid? actingTenantId = null)
    {
        RunId = runId;
        NodeId = nodeId;
        Quest = quest ?? throw new ArgumentNullException(nameof(quest));
        UpstreamExecutions = upstreamExecutions ?? new Dictionary<Guid, QuestNodeExecution>();
        ActingTenantId = actingTenantId;
        Node = quest.Nodes.FirstOrDefault(n => n.Id == nodeId)
            ?? throw new ArgumentException(
                $"Node {nodeId} not found in quest {quest.Id}.", nameof(nodeId));
    }

    /// <summary>Owning run id.</summary>
    public Guid RunId { get; }

    /// <summary>Definition node id being executed.</summary>
    public Guid NodeId { get; }

    /// <summary>Quest definition. Handlers MUST treat as read-only.</summary>
    public Quest Quest { get; }

    /// <summary>The definition node matching <see cref="NodeId"/>.</summary>
    public QuestNode Node { get; }

    /// <summary>
    /// Upstream node executions already produced by this run, keyed by source
    /// node id. Empty when no upstream nodes have completed (e.g. entry nodes).
    /// </summary>
    public IReadOnlyDictionary<Guid, QuestNodeExecution> UpstreamExecutions { get; }

    /// <summary>
    /// The tenant that drove the owning <see cref="QuestRun"/> via a tenant-driven
    /// child credential, or null when the run is user-driven
    /// (tenant-consent-delegation AC4/AC4b). Read from <c>QuestRun.ActingTenantId</c>
    /// at dispatch so it survives the async saga-worker hop. Tier-2 economic node
    /// handlers (Grant / Transfer / Refund / FungibleTokenCreate) pass it to the
    /// manager so the produced <c>BlockchainOperation</c> (or platform ASA create)
    /// carries it to the custody signing seam's live consent gate.
    /// </summary>
    public Guid? ActingTenantId { get; }
}

/// <summary>
/// Outcome of <c>IQuestNodeHandler.HandleAsync</c>. Decouples handler return
/// shape from the in-place <see cref="QuestNode"/> mutation that the previous
/// <c>AZOAResult&lt;QuestNode&gt;</c> contract implied. The manager translates
/// this into a <see cref="QuestNodeExecution"/> state transition.
/// </summary>
public sealed class QuestNodeHandlerResult
{
    public bool IsError { get; init; }

    /// <summary>Free-form message. On failure this becomes <see cref="QuestNodeExecution.Error"/>.</summary>
    public string? Message { get; init; }

    /// <summary>Serialized output (JSON). On success this becomes <see cref="QuestNodeExecution.Output"/>.</summary>
    public string? Output { get; init; }

    /// <summary>The broadcast tx hash, when this node put a tx on-chain. Lets the engine reconcile against chain truth before retrying (blockchain-recovery-and-portable-wallets §1.3).</summary>
    public string? TxHash { get; init; }

    /// <summary>The chain the tx was broadcast to (e.g. "Algorand"), for provider resolution during reconciliation.</summary>
    public string? ChainType { get; init; }

    /// <summary>False for an invalid-config failure that can never succeed on retry (nothing was broadcast). The engine fails such a node terminally without a chain probe or retry budget. Default true.</summary>
    public bool Retriable { get; init; } = true;

    public static QuestNodeHandlerResult Ok(string? output, string? message = null, string? txHash = null, string? chainType = null) =>
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
    public static QuestNodeHandlerResult Fail(string message, string? txHash = null, string? chainType = null) =>
        new() { IsError = true, Message = message, TxHash = txHash, ChainType = chainType };

    /// <summary>
    /// Invalid-config failure: nothing was broadcast and re-running can never
    /// succeed. <see cref="Retriable"/> is false so the engine fails the node
    /// terminally without a chain probe or retry budget
    /// (blockchain-recovery-and-portable-wallets §1 invalid-mode handling).
    /// </summary>
    public static QuestNodeHandlerResult Invalid(string message) =>
        new() { IsError = true, Message = message, Retriable = false };
}
