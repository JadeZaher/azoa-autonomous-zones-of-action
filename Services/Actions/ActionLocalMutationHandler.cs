using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Routes local effects through staged managers and atomic command receipts.</summary>
public sealed class ActionLocalMutationHandler(ActionNodeType nodeType, ActionLocalMutationService mutations)
    : IRecoverableActionNodeHandler
{
    /// <inheritdoc/>
    public ActionNodeType NodeType => nodeType;

    /// <inheritdoc/>
    public ActionExternalOperationReference CreateReference(ActionNodeExecutionContext context, string intentHash)
        => ActionLocalMutationService.Reference(context, intentHash);

    /// <inheritdoc/>
    public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
        => mutations.ExecuteAsync(context, ct);
}
