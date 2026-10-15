using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>
/// Handles <see cref="ActionNodeType.Condition"/> — relocated verbatim from ActionManager.
/// Condition nodes evaluate to a pass-through; the edge conditions on outgoing
/// edges handle the actual branching. No manager dependency.
/// </summary>
public sealed class ConditionNodeHandler : IActionNodeHandler
{
    public ActionNodeType NodeType => ActionNodeType.Condition;

    public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        // Condition nodes evaluate to a pass-through; the edge conditions
        // on outgoing edges handle the actual branching.
        var outputJson = context.Node.Config;
        return Task.FromResult(ActionNodeResults.Ok(outputJson));
    }
}
