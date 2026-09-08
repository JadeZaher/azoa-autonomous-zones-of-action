// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;
// Both imported namespaces declare an ActionNode: the graph node (Models.Actions) and
// the SurrealDB row (Persistence.SurrealDb.Models). The handler surface is the graph
// node; HoldLedger is why the persistence namespace is imported at all.
using ActionNode = AZOA.WebAPI.Models.Actions.ActionNode;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>
/// Handles <see cref="ActionNodeType.Hold"/> — Tier-2 chain action (D14 / W3.4).
/// Commits units out of a declared reserve partition into the platform-custodial holding
/// account for a named beneficiary, where they stay until
/// <see cref="HoldNodeConfig.ReleaseNotBefore"/> elapses.
/// <para>
/// The hold id is DERIVED from the run and the node unless the config names one, so the
/// node is idempotent by construction: re-running a step after a crash finds its own hold
/// and commits nothing twice. A config-supplied id is honoured because a durable saga may
/// legitimately want the id to be the tenant's own reference.
/// </para>
/// <para>
/// The beneficiary defaults to the RUN ACTOR, not the action owner. That is the
/// acting-user invariant this codebase enforces everywhere a node has an identity side
/// effect: a marketplace run holds units for the person who ran it, and an action author
/// cannot make every runner's stake land on themselves by omitting a field.
/// </para>
/// Requires a chain capability.
/// </summary>
public sealed class HoldNodeHandler : IActionNodeHandler
{
    private readonly IPlatformHold _holds;

    public HoldNodeHandler(IPlatformHold holds) => _holds = holds;

    public ActionNodeType NodeType => ActionNodeType.Hold;

    public bool RequiresChainCapability => true;

    /// <inheritdoc />
    public string? ResolveSettlementRailId(ActionNode node) =>
        HoldNodeRail.Resolve<HoldNodeConfig>(node, nameof(ActionNodeType.Hold), cfg => cfg.ChainType);

    public async Task<ActionNodeHandlerResult> HandleAsync(
        ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<HoldNodeConfig>(
                context.Node.Config, nameof(ActionNodeType.Hold), out var cfg, out var cfgError))
        {
            return ActionNodeResults.Invalid(cfgError);
        }

        if (string.IsNullOrWhiteSpace(cfg.ChainType))
            return ActionNodeResults.Invalid("[Hold] config must name the rail the hold settles on.");
        if (string.IsNullOrWhiteSpace(cfg.AssetId))
            return ActionNodeResults.Invalid("[Hold] config must name the asset being held.");
        if (string.IsNullOrWhiteSpace(cfg.ReservePartition))
            return ActionNodeResults.Invalid(
                "[Hold] config must name the declared reserve partition the units are drawn from.");
        if (!BigInteger.TryParse(
                cfg.Amount, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            || amount <= BigInteger.Zero)
        {
            return ActionNodeResults.Invalid(
                "[Hold] config must carry a positive whole base-unit amount as a decimal string.");
        }

        // Beneficiary: the config value when the author named one, otherwise the run
        // actor. Never the action OWNER — a marketplace run must not quietly hold a
        // runner's stake for the person who published the action.
        var beneficiary = cfg.BeneficiaryUserId is { } named && named != Guid.Empty
            ? named
            : context.ActingUserId;

        var request = new HoldOpenRequest(
            new HoldReference(cfg.ChainType!, HoldNodeIdentity.Resolve(cfg.HoldId, context)),
            cfg.AssetId!,
            amount,
            HoldFundingSource.Reserve,
            beneficiary,
            ReservePartition: cfg.ReservePartition,
            FunderUserId: Guid.Empty,
            ReleaseNotBefore: cfg.ReleaseNotBefore,
            AutoRelease: cfg.AutoRelease,
            // tenant-consent-delegation AC4: forward the run's acting tenant so the
            // partition's tenant scope is checked against the tenant that actually drove
            // the run, and so the custody seam's live consent gate fires on the release.
            ActingTenantId: context.ActingTenantId,
            Purpose: cfg.Purpose);

        var opened = await _holds.OpenAsync(request, ct);
        if (opened.IsError || opened.Result is null)
            return ActionNodeResults.Fail(opened.Message);

        return ActionNodeResults.Ok(
            HoldNodeResult.Serialize(opened.Result),
            chainType: cfg.ChainType);
    }
}

/// <summary>Config for <see cref="ActionNodeType.Hold"/>.</summary>
public sealed class HoldNodeConfig
{
    /// <summary>Rail the hold settles on (e.g. <c>Algorand</c>).</summary>
    public string? ChainType { get; set; }

    /// <summary>Optional explicit hold id; derived from the run and node when absent.</summary>
    public string? HoldId { get; set; }

    /// <summary>Asset being held. Must be one the platform reserve has a ledger row for.</summary>
    public string? AssetId { get; set; }

    /// <summary>Amount in the asset's base units, as a whole decimal string.</summary>
    public string? Amount { get; set; }

    /// <summary>Declared reserve partition the commitment is drawn from.</summary>
    public string? ReservePartition { get; set; }

    /// <summary>Who the units are held for. Null means the run actor.</summary>
    public Guid? BeneficiaryUserId { get; set; }

    /// <summary>The hold window. Null means the hold is releasable immediately.</summary>
    public DateTimeOffset? ReleaseNotBefore { get; set; }

    /// <summary>Whether the hosted sweep may release the hold once it is due.</summary>
    public bool AutoRelease { get; set; } = true;

    /// <summary>The caller's own reason for the hold.</summary>
    public string? Purpose { get; set; }
}

/// <summary>
/// The hold id a node uses. Deriving it from the run and the node is what makes a hold
/// node idempotent without a caller-supplied key: a retried step addresses the hold it
/// already opened instead of opening a second one over the same work.
/// </summary>
public static class HoldNodeIdentity
{
    /// <summary>The configured id when there is one, else <c>run:{runId}:node:{nodeId}</c>.</summary>
    public static string Resolve(string? configured, ActionNodeExecutionContext context) =>
        string.IsNullOrWhiteSpace(configured)
            ? $"run:{context.RunId:N}:node:{context.NodeId:N}"
            : configured.Trim();
}

/// <summary>Reads the settlement rail off a hold-family node's declared config.</summary>
public static class HoldNodeRail
{
    /// <summary>
    /// The rail the node's own config names, or null so the capability gate falls back to
    /// the platform default. A config that will not parse yields null rather than
    /// throwing: the gate runs before the handler, and a parse failure is the handler's
    /// error to report, not the gate's.
    /// </summary>
    public static string? Resolve<TConfig>(ActionNode node, string nodeTypeName, Func<TConfig, string?> pick)
        where TConfig : new()
    {
        if (!ActionNodeConfig.TryDeserialize<TConfig>(node.Config, nodeTypeName, out var cfg, out _))
            return null;

        var rail = pick(cfg);
        return string.IsNullOrWhiteSpace(rail) ? null : rail.Trim();
    }
}

/// <summary>
/// The flat output every hold-family node writes. One shape for all four so a downstream
/// <c>$from</c> binding reads the same fields whichever hold node produced them.
/// Every field is non-null (empty string for "none") so a binding never has to
/// distinguish an absent field from a null one.
/// </summary>
public sealed record HoldNodeResult(
    [property: JsonPropertyName("HoldId")] string HoldId,
    [property: JsonPropertyName("ChainType")] string ChainType,
    [property: JsonPropertyName("AssetId")] string AssetId,
    [property: JsonPropertyName("Amount")] string Amount,
    [property: JsonPropertyName("State")] string State,
    [property: JsonPropertyName("BeneficiaryUserId")] string BeneficiaryUserId,
    [property: JsonPropertyName("ReleaseNotBefore")] string ReleaseNotBefore,
    [property: JsonPropertyName("NextHoldId")] string NextHoldId,
    [property: JsonPropertyName("TransactionId")] string TransactionId,
    [property: JsonPropertyName("Replayed")] bool Replayed)
{
    /// <summary>Projects a hold outcome onto the node output shape.</summary>
    public static HoldNodeResult From(HoldOutcome outcome)
    {
        var hold = outcome.Hold;
        return new HoldNodeResult(
            hold.HoldId,
            hold.Chain,
            hold.AssetId,
            hold.Amount,
            hold.State,
            hold.BeneficiaryUserId,
            hold.ReleaseNotBefore?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            outcome.NextHold?.HoldId ?? hold.ChildHoldId ?? string.Empty,
            outcome.TransactionId ?? string.Empty,
            outcome.Replayed);
    }

    /// <summary>Serializes the outcome with the repo's node-output options.</summary>
    public static string Serialize(HoldOutcome outcome) =>
        System.Text.Json.JsonSerializer.Serialize(From(outcome), ActionNodeJson.Options);

    /// <summary>A representative row, for the golden output-schema test to serialize.</summary>
    public static HoldNodeResult Representative(HoldLedger hold) =>
        From(new HoldOutcome(hold, null, null, Replayed: false));
}
