namespace AZOA.WebAPI.Models.Quest;

/// <summary>
/// Per-(run, node) execution record. Replaces the in-place mutation of
/// <see cref="QuestNode"/>.State/Output/Error that prevented re-runs from
/// preserving the prior attempt's outputs.
/// </summary>
/// <remarks>
/// <para>
/// Natural key: <c>(RunId, NodeId)</c>. On a fork, the same execution row
/// can be referenced by both the parent and child run via the SurrealDB
/// <c>executes</c> RELATE edge for nodes whose
/// <c>ExecutionOrder &lt; forkPoint</c> (copy-by-reference, no duplication).
/// </para>
/// <para>
/// The conditional-update primitive <c>TryClaimPendingAsync</c> on
/// <see cref="AZOA.WebAPI.Interfaces.Stores.IQuestNodeExecutionStore"/>
/// preserves the [api-safety-hardening] G2 exactly-once semantic: it only
/// succeeds when current <see cref="State"/> equals <see cref="QuestNodeState.Pending"/>.
/// </para>
/// </remarks>
public class QuestNodeExecution
{
    /// <summary>Execution row identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning run.</summary>
    public Guid RunId { get; set; }

    /// <summary>Quest definition node this execution corresponds to.</summary>
    public Guid NodeId { get; set; }

    /// <summary>Current per-node lifecycle position. See <see cref="QuestNodeState"/>.</summary>
    public QuestNodeState State { get; set; } = QuestNodeState.Pending;

    /// <summary>
    /// Serialized <c>AZOAResult&lt;T&gt;</c> from the handler call. Null until
    /// the node reaches <see cref="QuestNodeState.Succeeded"/>.
    /// </summary>
    public string? Output { get; set; }

    /// <summary>Failure message when <see cref="State"/> is <see cref="QuestNodeState.Failed"/>.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// The broadcast tx hash when this node put a tx on-chain. Carried so the
    /// reconciliation sweep (or operator) can re-probe chain truth for a node
    /// parked in <c>AwaitingReconciliation</c>, and so a reconciled-to-success
    /// node records the hash that landed (blockchain-recovery-and-portable-wallets §1.3).
    /// </summary>
    public string? TxHash { get; set; }

    /// <summary>The chain the tx was broadcast to (e.g. "Algorand"), for provider resolution during reconciliation.</summary>
    public string? ChainType { get; set; }

    /// <summary>Wall-clock time at which the row entered <see cref="QuestNodeState.Running"/>.</summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Wall-clock time at which the row reached a terminal state. Null while non-terminal.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>
    /// Field-by-field copy. Used by the in-memory store to hand defensive
    /// clones to readers so accidental caller-side mutation of a returned
    /// execution can't leak back into the store's internal map (HIGH#7).
    /// </summary>
    public QuestNodeExecution Clone() => new()
    {
        Id        = Id,
        RunId     = RunId,
        NodeId    = NodeId,
        State     = State,
        Output    = Output,
        Error     = Error,
        TxHash    = TxHash,
        ChainType = ChainType,
        StartedAt = StartedAt,
        EndedAt   = EndedAt,
    };
}
