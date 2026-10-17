using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>
/// Declares the NFT <see cref="ActionNodeType.Refund"/>; effects run only through <see cref="ActionNftOperationService"/>,
/// which the registry wraps around this declaration (the direct body was unreachable and is pruned).
/// </summary>
public sealed class RefundNodeHandler : IActionNodeHandler
{
    /// <inheritdoc/>
    public ActionNodeType NodeType => ActionNodeType.Refund;
    /// <inheritdoc/>
    public bool RequiresChainCapability => true;

    /// <inheritdoc/>
    public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
        => Task.FromResult(ActionNodeResults.Invalid("NFT refunds run only through the durable NFT lifecycle admission."));
}

/// <summary>
/// Declares <see cref="ActionNodeType.HoldRefund"/> (Tier-2, D14 / W3.4), distinct from the NFT
/// <see cref="ActionNodeType.Refund"/>. Effects run only through <see cref="ActionHoldOperationService"/>.
/// </summary>
public sealed class HoldRefundNodeHandler : IActionNodeHandler
{
    public ActionNodeType NodeType => ActionNodeType.HoldRefund;

    public bool RequiresChainCapability => true;

    /// <inheritdoc />
    public string? ResolveSettlementRailId(ActionNode node) =>
        HoldNodeRail.Resolve<HoldRefundNodeConfig>(node, nameof(ActionNodeType.HoldRefund), cfg => cfg.ChainType);

    /// <inheritdoc />
    public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
        => Task.FromResult(HoldNodeRail.RequiresDurableAdmission());
}

/// <summary>Config for <see cref="ActionNodeType.HoldRefund"/>.</summary>
public sealed class HoldRefundNodeConfig
{
    public AZOA.WebAPI.Core.ChainNetwork? Network { get; set; }
    public Guid? RecipientWalletId { get; set; }
    public string? HoldRecordId { get; set; }
    /// <summary>Rail the hold settles on (e.g. <c>Algorand</c>).</summary>
    public string? ChainType { get; set; }

    /// <summary>The hold to give back. Mutually exclusive with <see cref="HoldNodeName"/>.</summary>
    public string? HoldId { get; set; }

    /// <summary>Name of the node in this action that opened the hold, when no id is given.</summary>
    public string? HoldNodeName { get; set; }

    /// <summary>Why the hold is being given back; recorded on the ledger row.</summary>
    public string? Reason { get; set; }
}

