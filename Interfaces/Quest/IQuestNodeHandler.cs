using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Interfaces.QuestExecution;

/// <summary>
/// Executes one <see cref="QuestNodeType"/>; exactly one handler per type.
/// </summary>
/// <remarks>
/// <para>
/// Contract migration: previously took <c>(Quest, QuestNode)</c> and mutated
/// <c>QuestNode.State/Output/Error</c> in place. After the
/// <c>quest-temporal-fork-model</c> track the handler is run-aware via
/// <see cref="QuestNodeExecutionContext"/>; runtime state is written by
/// <c>QuestManager</c> onto the per-(run, node) <see cref="QuestNodeExecution"/>
/// row. Handlers no longer mutate the quest <i>definition</i>.
/// </para>
/// </remarks>
public interface IQuestNodeHandler
{
    /// <summary>The single node type this handler dispatches.</summary>
    QuestNodeType NodeType { get; }

    /// <summary>
    /// True if this node moves value and therefore needs a settlement capability on
    /// the rail it lands on. The engine refuses to run such a node pre-execution when
    /// the actor has no usable destination there (fails closed). Default false: holon
    /// transforms are pure metadata and settle nothing. Only the Tier-2
    /// value-action subset (Swap/Grant/Transfer/Refund/Bridge/Back/FungibleTokenCreate)
    /// overrides this to true.
    /// </summary>
    bool RequiresChainCapability => false;

    /// <summary>
    /// The value rail this node will settle on, read from its DECLARED config — e.g. a
    /// Bridge node settles on its source chain, a FungibleTokenCreate on its configured
    /// chain. Null means "whatever the platform default rail is", which is the right
    /// answer for a node whose config names no rail.
    /// <para>Read at the capability gate, which asks whether the actor can receive value
    /// THERE rather than anywhere. See <c>Services/Quest/AGENTS.md</c> §rail-capability.</para>
    /// </summary>
    string? ResolveSettlementRailId(QuestNode node) => null;

    /// <summary>
    /// Executes the node identified by <paramref name="context"/> and returns
    /// either a success <see cref="QuestNodeHandlerResult"/> carrying serialized
    /// output JSON, or a failure carrying the error message. The manager turns
    /// the result into a <see cref="QuestNodeExecution"/> transition.
    /// </summary>
    Task<QuestNodeHandlerResult> HandleAsync(
        QuestNodeExecutionContext context,
        CancellationToken ct = default);
}
