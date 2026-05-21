using OASIS.WebAPI.Models.Quest;

namespace OASIS.WebAPI.Interfaces.QuestExecution;

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
    /// Executes the node identified by <paramref name="context"/> and returns
    /// either a success <see cref="QuestNodeHandlerResult"/> carrying serialized
    /// output JSON, or a failure carrying the error message. The manager turns
    /// the result into a <see cref="QuestNodeExecution"/> transition.
    /// </summary>
    Task<QuestNodeHandlerResult> HandleAsync(
        QuestNodeExecutionContext context,
        CancellationToken ct = default);
}
