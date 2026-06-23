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
    /// True if this node requires a chain capability (a wallet bound to the
    /// run). The engine refuses to run such a node pre-execution when no
    /// wallet/capability is bound (fails closed). Default false: holon
    /// transforms are pure metadata and need no chain. Only the Tier-2
    /// chain-action subset (Swap/Grant/Transfer/Refund) overrides this to true.
    /// </summary>
    bool RequiresChainCapability => false;

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
