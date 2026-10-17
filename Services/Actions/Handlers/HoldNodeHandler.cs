// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Text.Json.Serialization;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
// Both imported namespaces declare an ActionNode: the graph node (Models.Actions) and
// the SurrealDB row (Persistence.SurrealDb.Models). The handler surface is the graph node.
using ActionNode = AZOA.WebAPI.Models.Actions.ActionNode;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>
/// Declares <see cref="ActionNodeType.Hold"/> (Tier-2, D14 / W3.4). Effects run only through
/// <see cref="ActionHoldOperationService"/>; see Services/Hold/AGENTS.md §node-quartet.
/// </summary>
public sealed class HoldNodeHandler : IActionNodeHandler
{
    public ActionNodeType NodeType => ActionNodeType.Hold;

    public bool RequiresChainCapability => true;

    /// <inheritdoc />
    public string? ResolveSettlementRailId(ActionNode node) =>
        HoldNodeRail.Resolve<HoldNodeConfig>(node, nameof(ActionNodeType.Hold), cfg => cfg.ChainType);

    /// <inheritdoc />
    public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
        => Task.FromResult(HoldNodeRail.RequiresDurableAdmission());
}

/// <summary>Config for <see cref="ActionNodeType.Hold"/>.</summary>
public sealed class HoldNodeConfig
{
    /// <summary>Explicit physical network the hold is admitted on.</summary>
    public AZOA.WebAPI.Core.ChainNetwork? Network { get; set; }

    /// <summary>Beneficiary wallet an automatic release pays; required with <see cref="AutoRelease"/>.</summary>
    public Guid? RecipientWalletId { get; set; }

    /// <summary>Frozen automatic-release deadline and independent cost caps.</summary>
    public HoldWorkflowMandateSelection? Mandate { get; set; }

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

    /// <summary>Whether an explicitly disclosed release mandate is requested for the hold.</summary>
    public bool AutoRelease { get; set; }

    /// <summary>The caller's own reason for the hold.</summary>
    public string? Purpose { get; set; }
}

/// <summary>Reads the settlement rail off a hold-family node's declared config.</summary>
public static class HoldNodeRail
{
    /// <summary>
    /// The rail the node's own config names, or null so the capability gate falls back to
    /// the platform default. A config that will not parse yields null: the gate runs before
    /// the durable adapter, which reports the parse failure.
    /// </summary>
    public static string? Resolve<TConfig>(ActionNode node, string nodeTypeName, Func<TConfig, string?> pick)
        where TConfig : new()
    {
        if (!ActionNodeConfig.TryDeserialize<TConfig>(node.Config, nodeTypeName, out var cfg, out _))
            return null;
        var rail = pick(cfg);
        return string.IsNullOrWhiteSpace(rail) ? null : rail.Trim();
    }

    /// <summary>The refusal a hold-family descriptor returns if invoked outside durable admission.</summary>
    public static ActionNodeHandlerResult RequiresDurableAdmission()
        => ActionNodeResults.Invalid("Hold effects run only through durable workflow admission and the original custody command.");
}

/// <summary>
/// The flat output every hold-family node writes. One shape for all four so a downstream
/// <c>$from</c> binding reads the same fields whichever hold node produced them.
/// Every field is non-null (empty string for "none").
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
    [property: JsonPropertyName("Replayed")] bool Replayed,
    [property: JsonPropertyName("HoldRecordId")] string HoldRecordId = "",
    [property: JsonPropertyName("NextHoldRecordId")] string NextHoldRecordId = "",
    [property: JsonPropertyName("OperationId")] string OperationId = "")
{
    /// <summary>Projects one immutable completed hold command receipt onto the node output shape.</summary>
    public static HoldNodeResult From(HoldCommandView view, HoldCommandIntent intent) => new(
        view.HoldId,
        view.Asset.ChainType,
        view.Asset.AssetId,
        view.QuantityBaseUnits,
        view.Kind switch { "Open" => "Held", "Refund" => "Refunded", "ReleaseIntoHold" => "ReleasedIntoHold", _ => "Released" },
        view.BeneficiaryUserId.ToString("D"),
        intent.Definition?.ReleaseNotBefore?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
        intent.NextHoldId ?? string.Empty,
        view.TransactionId ?? string.Empty,
        Replayed: true,
        view.HoldRecordId,
        view.NextHoldRecordId ?? string.Empty,
        view.OperationId);

    /// <summary>Serializes the result with the repo's node-output options.</summary>
    public static string Serialize(HoldNodeResult result) =>
        System.Text.Json.JsonSerializer.Serialize(result, ActionNodeJson.Options);

    /// <summary>A representative row, for the golden output-schema test to serialize.</summary>
    public static HoldNodeResult Representative(HoldLedger hold) => new(
        hold.HoldId, hold.Chain, hold.AssetId, hold.Amount, hold.State, hold.BeneficiaryUserId,
        hold.ReleaseNotBefore?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
        hold.ChildHoldId ?? string.Empty, hold.SettlementTransactionId ?? string.Empty, false,
        hold.Id, string.Empty, hold.OpeningCommandId ?? string.Empty);
}
