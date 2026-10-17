using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <inheritdoc/>
public sealed class EmitNodeHandler(ActionEmitService operations) : IRecoverableActionNodeHandler
{
    /// <inheritdoc/>
    public ActionNodeType NodeType => ActionNodeType.Emit;

    /// <inheritdoc/>
    public ActionExternalOperationReference CreateReference(ActionNodeExecutionContext context, string intentHash)
        => ActionEmitService.Reference(context, intentHash);

    /// <inheritdoc/>
    public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
        => operations.ExecuteAsync(context, ct);
}
