// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Hold;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>
/// Handles <see cref="ActionNodeType.Release"/> — Tier-2 chain action (D14 / W3.4).
/// Hands a due hold's units to its beneficiary. This is the ONLY node that moves units
/// out of the holding account, and it is refused before the hold's window elapses, so a
/// graph cannot pay a stake out early by routing around the window.
/// <para>
/// The beneficiary is the one frozen on the hold at open time; nothing in this node's
/// config can redirect it. That is deliberate — a release whose recipient came from the
/// releasing node would be a second transfer wearing a release's name, which is exactly
/// the drain vector the Refund node's linkage guard exists to close.
/// </para>
/// Requires a chain capability.
/// </summary>
public sealed class ReleaseNodeHandler : IActionNodeHandler
{
    private readonly IPlatformHold _holds;

    public ReleaseNodeHandler(IPlatformHold holds) => _holds = holds;

    public ActionNodeType NodeType => ActionNodeType.Release;

    public bool RequiresChainCapability => true;

    /// <inheritdoc />
    public string? ResolveSettlementRailId(ActionNode node) =>
        HoldNodeRail.Resolve<ReleaseNodeConfig>(node, nameof(ActionNodeType.Release), cfg => cfg.ChainType);

    public async Task<ActionNodeHandlerResult> HandleAsync(
        ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<ReleaseNodeConfig>(
                context.Node.Config, nameof(ActionNodeType.Release), out var cfg, out var cfgError))
        {
            return ActionNodeResults.Invalid(cfgError);
        }

        if (string.IsNullOrWhiteSpace(cfg.ChainType))
            return ActionNodeResults.Invalid("[Release] config must name the rail the hold settles on.");

        if (!HoldNodeTargetResolver.TryResolve(
                cfg.HoldId, cfg.HoldNodeName, context, nameof(ActionNodeType.Release),
                out var holdId, out var resolveError))
        {
            return ActionNodeResults.Invalid(resolveError);
        }

        var released = await _holds.ReleaseAsync(
            new HoldSettlementRequest(
                new HoldReference(cfg.ChainType!, holdId),
                // tenant-consent-delegation AC4: forward the run's acting tenant so the
                // custody seam's live consent gate fires before the payout is signed.
                ActingTenantId: context.ActingTenantId,
                Reason: cfg.Reason),
            ct);

        if (released.IsError || released.Result is null)
            return ActionNodeResults.Fail(
                released.Message,
                txHash: released.Result?.TransactionId,
                chainType: cfg.ChainType);

        // The payout hash is forwarded on success so the reconcile-before-retry engine can
        // verify chain truth rather than re-sending a payment that already landed.
        return ActionNodeResults.Ok(
            HoldNodeResult.Serialize(released.Result),
            txHash: released.Result.TransactionId,
            chainType: cfg.ChainType);
    }
}

/// <summary>Config for <see cref="ActionNodeType.Release"/>.</summary>
public sealed class ReleaseNodeConfig
{
    public AZOA.WebAPI.Core.ChainNetwork? Network { get; set; }
    public Guid? RecipientWalletId { get; set; }
    public string? HoldRecordId { get; set; }
    /// <summary>Rail the hold settles on (e.g. <c>Algorand</c>).</summary>
    public string? ChainType { get; set; }

    /// <summary>The hold to release. Mutually exclusive with <see cref="HoldNodeName"/>.</summary>
    public string? HoldId { get; set; }

    /// <summary>Name of the node in this action that opened the hold, when no id is given.</summary>
    public string? HoldNodeName { get; set; }

    /// <summary>Why the hold is being released; recorded on the ledger row.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// Handles <see cref="ActionNodeType.ReleaseIntoHold"/> — Tier-2 chain action (D14 / W3.4).
/// Closes one hold by opening the NEXT one over the same units.
/// <para>
/// It exists because approving completed work is a release into a SECOND hold — the
/// worker's own window — and a plain <see cref="ActionNodeType.Release"/> cannot express
/// that: a release ENDS the commitment, while the second window has to keep it. Nothing
/// moves on chain here; only who the units are held for, and until when, changes.
/// </para>
/// <para>
/// The next window may never be earlier than the one it continues. That check lives in
/// the hold service, so the graph cannot use a chain of instant holds as a way out of the
/// funder's window.
/// </para>
/// Requires a chain capability: the units it re-designates are custodial units on a rail,
/// and the run actor must be able to settle there.
/// </summary>
public sealed class ReleaseIntoHoldNodeHandler : IActionNodeHandler
{
    private readonly IPlatformHold _holds;

    public ReleaseIntoHoldNodeHandler(IPlatformHold holds) => _holds = holds;

    public ActionNodeType NodeType => ActionNodeType.ReleaseIntoHold;

    public bool RequiresChainCapability => true;

    /// <inheritdoc />
    public string? ResolveSettlementRailId(ActionNode node) =>
        HoldNodeRail.Resolve<ReleaseIntoHoldNodeConfig>(
            node, nameof(ActionNodeType.ReleaseIntoHold), cfg => cfg.ChainType);

    public async Task<ActionNodeHandlerResult> HandleAsync(
        ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<ReleaseIntoHoldNodeConfig>(
                context.Node.Config, nameof(ActionNodeType.ReleaseIntoHold), out var cfg, out var cfgError))
        {
            return ActionNodeResults.Invalid(cfgError);
        }

        if (string.IsNullOrWhiteSpace(cfg.ChainType))
            return ActionNodeResults.Invalid("[ReleaseIntoHold] config must name the rail the hold settles on.");

        if (!HoldNodeTargetResolver.TryResolve(
                cfg.HoldId, cfg.HoldNodeName, context, nameof(ActionNodeType.ReleaseIntoHold),
                out var holdId, out var resolveError))
        {
            return ActionNodeResults.Invalid(resolveError);
        }

        // Beneficiary of the NEXT hold: the config value when the author named one,
        // otherwise the run actor — the same acting-user invariant the Hold node keeps.
        var nextBeneficiary = cfg.NextBeneficiaryUserId is { } named && named != Guid.Empty
            ? named
            : context.ActingUserId;

        var continued = await _holds.ReleaseIntoHoldAsync(
            new HoldChainRequest(
                new HoldReference(cfg.ChainType!, holdId),
                HoldNodeTargetResolver.ResolveNextHoldId(cfg.NextHoldId, context),
                nextBeneficiary,
                ReleaseNotBefore: cfg.ReleaseNotBefore,
                AutoRelease: cfg.AutoRelease,
                ActingTenantId: context.ActingTenantId,
                Purpose: cfg.Purpose),
            ct);

        if (continued.IsError || continued.Result is null)
            return ActionNodeResults.Fail(continued.Message, chainType: cfg.ChainType);

        return ActionNodeResults.Ok(
            HoldNodeResult.Serialize(continued.Result),
            chainType: cfg.ChainType);
    }
}

/// <summary>Config for <see cref="ActionNodeType.ReleaseIntoHold"/>.</summary>
public sealed class ReleaseIntoHoldNodeConfig
{
    public AZOA.WebAPI.Core.ChainNetwork? Network { get; set; }
    public Guid? RecipientWalletId { get; set; }
    public string? HoldRecordId { get; set; }
    public AZOA.WebAPI.Models.Blockchain.HoldWorkflowMandateSelection? Mandate { get; set; }
    /// <summary>Rail the hold settles on (e.g. <c>Algorand</c>).</summary>
    public string? ChainType { get; set; }

    /// <summary>The hold being continued. Mutually exclusive with <see cref="HoldNodeName"/>.</summary>
    public string? HoldId { get; set; }

    /// <summary>Name of the node in this action that opened the hold, when no id is given.</summary>
    public string? HoldNodeName { get; set; }

    /// <summary>Id of the hold that continues it; derived from this node when absent.</summary>
    public string? NextHoldId { get; set; }

    /// <summary>Who the units are held for next. Null means the run actor.</summary>
    public Guid? NextBeneficiaryUserId { get; set; }

    /// <summary>
    /// The next window. It may never be earlier than the window it continues; the hold
    /// service refuses that outright.
    /// </summary>
    public DateTimeOffset? ReleaseNotBefore { get; set; }

    /// <summary>Whether the hosted sweep may release the next hold once it is due.</summary>
    public bool AutoRelease { get; set; } = true;

    /// <summary>The caller's own reason for the next hold.</summary>
    public string? Purpose { get; set; }
}

/// <summary>
/// Turns a hold-family node's config into the hold id it addresses. Naming the node that
/// opened the hold is the ergonomic form — a graph author should not have to reproduce
/// the derived id by hand — and it resolves through the SAME derivation the opening node
/// used, so the two can never disagree.
/// </summary>
public static class HoldNodeTargetResolver
{
    /// <summary>
    /// Resolves the addressed hold id from an explicit id or a named node in this action.
    /// Returns false with a caller-shaped error when neither is usable.
    /// </summary>
    public static bool TryResolve(
        string? configuredHoldId,
        string? holdNodeName,
        ActionNodeExecutionContext context,
        string nodeTypeName,
        out string holdId,
        out string error)
    {
        holdId = string.Empty;
        error = string.Empty;

        if (!string.IsNullOrWhiteSpace(configuredHoldId))
        {
            if (!string.IsNullOrWhiteSpace(holdNodeName))
            {
                error =
                    $"[{nodeTypeName}] config names both a hold id and a hold node. Two answers to " +
                    "'which hold' is one too many; give exactly one.";
                return false;
            }

            holdId = configuredHoldId.Trim();
            return true;
        }

        if (string.IsNullOrWhiteSpace(holdNodeName))
        {
            error =
                $"[{nodeTypeName}] config must name the hold it settles — either a hold id, or the node in " +
                "this action that opened it.";
            return false;
        }

        var wanted = holdNodeName.Trim();
        var source = context.ActionDefinition.Nodes.FirstOrDefault(node =>
            string.Equals(node.Name, wanted, StringComparison.Ordinal)
            && node.NodeType is ActionNodeType.Hold or ActionNodeType.ReleaseIntoHold);

        if (source is null)
        {
            error =
                $"[{nodeTypeName}] config names hold node '{wanted}', but this action has no Hold or " +
                "ReleaseIntoHold node with that name.";
            return false;
        }

        // A Hold node's id is derived from the run and that node; a ReleaseIntoHold node
        // produces the CHILD hold, so its id is the one its own config declares (or the
        // same derivation with the child suffix).
        if (source.NodeType == ActionNodeType.Hold)
        {
            if (!ActionNodeConfig.TryDeserialize<HoldNodeConfig>(
                    source.Config, nameof(ActionNodeType.Hold), out var holdConfig, out _))
            {
                error = $"[{nodeTypeName}] could not read the config of hold node '{wanted}'.";
                return false;
            }

            holdId = string.IsNullOrWhiteSpace(holdConfig.HoldId)
                ? DerivedHoldId(context.RunId, source.Id)
                : holdConfig.HoldId.Trim();
            return true;
        }

        if (!ActionNodeConfig.TryDeserialize<ReleaseIntoHoldNodeConfig>(
                source.Config, nameof(ActionNodeType.ReleaseIntoHold), out var chainConfig, out _))
        {
            error = $"[{nodeTypeName}] could not read the config of hold node '{wanted}'.";
            return false;
        }

        holdId = string.IsNullOrWhiteSpace(chainConfig.NextHoldId)
            ? DerivedNextHoldId(context.RunId, source.Id)
            : chainConfig.NextHoldId.Trim();
        return true;
    }

    /// <summary>The child hold id a ReleaseIntoHold node opens: configured, else derived.</summary>
    public static string ResolveNextHoldId(string? configured, ActionNodeExecutionContext context) =>
        string.IsNullOrWhiteSpace(configured)
            ? DerivedNextHoldId(context.RunId, context.NodeId)
            : configured.Trim();

    private static string DerivedHoldId(Guid runId, Guid nodeId) => $"run:{runId:N}:node:{nodeId:N}";

    private static string DerivedNextHoldId(Guid runId, Guid nodeId) =>
        $"{DerivedHoldId(runId, nodeId)}:next";
}
