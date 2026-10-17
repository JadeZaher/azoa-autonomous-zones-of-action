// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>
/// Declares <see cref="ActionNodeType.Release"/> (Tier-2, D14 / W3.4). The beneficiary is the one
/// frozen on the original liability; effects run only through <see cref="ActionHoldOperationService"/>.
/// </summary>
public sealed class ReleaseNodeHandler : IActionNodeHandler
{
    public ActionNodeType NodeType => ActionNodeType.Release;

    public bool RequiresChainCapability => true;

    /// <inheritdoc />
    public string? ResolveSettlementRailId(ActionNode node) =>
        HoldNodeRail.Resolve<ReleaseNodeConfig>(node, nameof(ActionNodeType.Release), cfg => cfg.ChainType);

    /// <inheritdoc />
    public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
        => Task.FromResult(HoldNodeRail.RequiresDurableAdmission());
}

/// <summary>Config for <see cref="ActionNodeType.Release"/>.</summary>
public sealed class ReleaseNodeConfig
{
    /// <summary>Explicit physical network of a human hold reference.</summary>
    public AZOA.WebAPI.Core.ChainNetwork? Network { get; set; }

    /// <summary>The original beneficiary's wallet the release pays.</summary>
    public Guid? RecipientWalletId { get; set; }

    /// <summary>Network-qualified liability record. Exactly one of this, <see cref="HoldId"/> or <see cref="HoldNodeName"/>.</summary>
    public string? HoldRecordId { get; set; }

    /// <summary>Rail the hold settles on (e.g. <c>Algorand</c>).</summary>
    public string? ChainType { get; set; }

    /// <summary>The owner's human hold reference.</summary>
    public string? HoldId { get; set; }

    /// <summary>Name of the succeeded Hold or ReleaseIntoHold node in this run that produced the liability.</summary>
    public string? HoldNodeName { get; set; }

    /// <summary>Why the hold is being released; recorded on the command.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// Declares <see cref="ActionNodeType.ReleaseIntoHold"/> (Tier-2, D14 / W3.4): closes one liability by opening
/// the next over the same units, never with an earlier window. Effects run only through
/// <see cref="ActionHoldOperationService"/>.
/// </summary>
public sealed class ReleaseIntoHoldNodeHandler : IActionNodeHandler
{
    public ActionNodeType NodeType => ActionNodeType.ReleaseIntoHold;

    public bool RequiresChainCapability => true;

    /// <inheritdoc />
    public string? ResolveSettlementRailId(ActionNode node) =>
        HoldNodeRail.Resolve<ReleaseIntoHoldNodeConfig>(node, nameof(ActionNodeType.ReleaseIntoHold), cfg => cfg.ChainType);

    /// <inheritdoc />
    public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
        => Task.FromResult(HoldNodeRail.RequiresDurableAdmission());
}

/// <summary>Config for <see cref="ActionNodeType.ReleaseIntoHold"/>.</summary>
public sealed class ReleaseIntoHoldNodeConfig
{
    /// <summary>Explicit physical network of a human hold reference.</summary>
    public AZOA.WebAPI.Core.ChainNetwork? Network { get; set; }

    /// <summary>Next beneficiary's wallet an automatic child release pays; required with <see cref="AutoRelease"/>.</summary>
    public Guid? RecipientWalletId { get; set; }

    /// <summary>Network-qualified liability record. Exactly one of this, <see cref="HoldId"/> or <see cref="HoldNodeName"/>.</summary>
    public string? HoldRecordId { get; set; }

    /// <summary>Frozen automatic child release deadline and independent cost caps.</summary>
    public AZOA.WebAPI.Models.Blockchain.HoldWorkflowMandateSelection? Mandate { get; set; }

    /// <summary>Rail the hold settles on (e.g. <c>Algorand</c>).</summary>
    public string? ChainType { get; set; }

    /// <summary>The owner's human reference of the hold being continued.</summary>
    public string? HoldId { get; set; }

    /// <summary>Name of the succeeded Hold or ReleaseIntoHold node in this run that produced the liability.</summary>
    public string? HoldNodeName { get; set; }

    /// <summary>Id of the hold that continues it; derived from this node when absent.</summary>
    public string? NextHoldId { get; set; }

    /// <summary>Who the units are held for next. Null means the run actor.</summary>
    public Guid? NextBeneficiaryUserId { get; set; }

    /// <summary>The next window; never earlier than the window it continues.</summary>
    public DateTimeOffset? ReleaseNotBefore { get; set; }

    /// <summary>Whether an explicitly disclosed release mandate is requested for the next hold.</summary>
    public bool AutoRelease { get; set; }

    /// <summary>The caller's own reason for the next hold.</summary>
    public string? Purpose { get; set; }
}
