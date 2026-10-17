using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Admits NFT action correlation before entering the durable domain lifecycle.</summary>
public sealed class ActionNftOperationHandler(IActionNodeHandler inner, ActionNftOperationService operations)
    : IRecoverableActionNodeHandler
{
    /// <inheritdoc/>
    public ActionNodeType NodeType => inner.NodeType;
    /// <inheritdoc/>
    public bool RequiresChainCapability => inner.RequiresChainCapability;
    /// <inheritdoc/>
    public string? ResolveSettlementRailId(ActionNode node) => inner.ResolveSettlementRailId(node);
    /// <inheritdoc/>
    public ActionExternalOperationReference CreateReference(ActionNodeExecutionContext context, string intentHash)
        => ActionNftOperationService.Reference(context, intentHash);
    /// <inheritdoc/>
    public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
        => operations.ExecuteAsync(context, ct);
}
