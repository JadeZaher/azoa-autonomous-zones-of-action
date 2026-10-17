using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Dex;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.Swap"/> through the durable DEX journal; see Services/Dex/AGENTS.md §action swaps.</summary>
public sealed class SwapNodeHandler(SwapActionResolver swaps) : IRecoverableActionNodeHandler
{
    /// <inheritdoc/>
    public ActionNodeType NodeType => ActionNodeType.Swap;

    /// <inheritdoc/>
    public bool RequiresChainCapability => true;

    /// <inheritdoc/>
    public ActionExternalOperationReference CreateReference(ActionNodeExecutionContext context, string intentHash)
        => SwapActionResolver.Reference(context, intentHash);

    /// <inheritdoc/>
    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<SwapNodeConfig>(context.Node.Config, nameof(ActionNodeType.Swap), out var config, out var error) ||
            config.Quote is null)
            return ActionNodeResults.Invalid(error ?? "A typed exact-input swap quote request is required.");
        return await swaps.ExecuteAsync(context, ct);
    }
}
