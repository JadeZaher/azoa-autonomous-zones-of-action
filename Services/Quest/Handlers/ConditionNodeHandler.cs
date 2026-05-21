using OASIS.WebAPI.Interfaces.QuestExecution;
using OASIS.WebAPI.Models.Quest;

namespace OASIS.WebAPI.Services.Quest.Handlers;

/// <summary>
/// Handles <see cref="QuestNodeType.Condition"/> — relocated verbatim from QuestManager.
/// Condition nodes evaluate to a pass-through; the edge conditions on outgoing
/// edges handle the actual branching. No manager dependency.
/// </summary>
public sealed class ConditionNodeHandler : IQuestNodeHandler
{
    public QuestNodeType NodeType => QuestNodeType.Condition;

    public Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        // Condition nodes evaluate to a pass-through; the edge conditions
        // on outgoing edges handle the actual branching.
        var outputJson = context.Node.Config;
        return Task.FromResult(QuestNodeResults.Ok(outputJson));
    }
}
