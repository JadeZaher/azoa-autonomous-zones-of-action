using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>The durable WTT route for Bridge/Back nodes whose config carries <c>wtt</c>; see Services/Actions/AGENTS.md §wtt-route.</summary>
public interface IActionWttRoute
{
    /// <summary>True when the node's config selects the WTT route (independent of whether WTT is enabled).</summary>
    bool Applies(ActionNode node);

    /// <summary>The exact workflow signing scopes the route needs (besides <c>action:execute</c>).</summary>
    IReadOnlyList<string> SigningScopes { get; }

    /// <summary>The source chain the value leaves from, used by the value-rail capability gate.</summary>
    string? SettlementRailId(ActionNode node);

    /// <summary>The recoverable operation identity (domain <c>wtt</c>) for the frozen node input.</summary>
    ActionExternalOperationReference CreateReference(ActionNodeExecutionContext context, string intentHash);

    /// <summary>Quotes/admits/continues the transfer under the frozen workflow authority.</summary>
    Task<ActionNodeHandlerResult> ExecuteAsync(ActionNodeExecutionContext context, CancellationToken ct);
}

/// <summary>Recoverable Bridge/Back handler on the WTT route; the registry selects it per node config.</summary>
internal sealed class ActionWttNodeHandler(ActionNodeType nodeType, IActionWttRoute route) : IRecoverableActionNodeHandler
{
    /// <inheritdoc/>
    public ActionNodeType NodeType => nodeType;

    /// <inheritdoc/>
    public bool RequiresChainCapability => true;

    /// <inheritdoc/>
    public string? ResolveSettlementRailId(ActionNode node) => route.SettlementRailId(node);

    /// <inheritdoc/>
    public IReadOnlyList<string>? ResolveSigningScopes(ActionNode node) => route.SigningScopes;

    /// <inheritdoc/>
    public ActionExternalOperationReference CreateReference(ActionNodeExecutionContext context, string intentHash)
        => route.CreateReference(context, intentHash);

    /// <inheritdoc/>
    public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
        => route.ExecuteAsync(context, ct);
}
