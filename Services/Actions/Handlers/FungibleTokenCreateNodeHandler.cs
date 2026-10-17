using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <inheritdoc/>
public sealed class FungibleTokenCreateNodeHandler(ActionFungibleCreationService operations) : IRecoverableActionNodeHandler
{
    /// <inheritdoc/>
    public ActionNodeType NodeType => ActionNodeType.FungibleTokenCreate;
    /// <inheritdoc/>
    public bool RequiresChainCapability => true;
    /// <inheritdoc/>
    public string? ResolveSettlementRailId(ActionNode node)
        => ActionNodeConfig.TryDeserialize<FungibleTokenCreateNodeConfig>(node.Config, nameof(ActionNodeType.FungibleTokenCreate), out var cfg, out _) ? cfg.ChainType : null;
    /// <inheritdoc/>
    public ActionExternalOperationReference CreateReference(ActionNodeExecutionContext context, string intentHash)
        => ActionFungibleCreationService.Reference(context, intentHash);
    /// <inheritdoc/>
    public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
        => operations.ExecuteAsync(context, ct);
}
