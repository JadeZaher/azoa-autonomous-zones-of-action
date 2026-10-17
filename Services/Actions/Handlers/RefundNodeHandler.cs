using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <inheritdoc/>
public sealed class RefundNodeHandler(INftLifecycleService lifecycle) : IActionNodeHandler
{
    /// <inheritdoc/>
    public ActionNodeType NodeType => ActionNodeType.Refund;
    /// <inheritdoc/>
    public bool RequiresChainCapability => true;

    /// <inheritdoc/>
    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<RefundNodeConfig>(context.Node.Config, nameof(ActionNodeType.Refund), out var cfg, out var error))
            return ActionNodeResults.Invalid(error);
        foreach (var (sourceId, execution) in context.AllRunExecutions)
        {
            var node = context.ActionDefinition.Nodes.FirstOrDefault(item => item.Id == sourceId);
            if (execution.State != ActionNodeState.Succeeded || node?.NodeType != ActionNodeType.Transfer) continue;
            if (!ActionNodeConfig.TryDeserialize<TransferNodeConfig>(node.Config, nameof(ActionNodeType.Transfer), out var transfer, out _)
                || transfer.NftId != cfg.NftId) continue;
            var originalId = NftLifecycleIdentity.ForRequest(context.ActingUserId, "Transfer", $"{context.RunId:N}:{sourceId:N}", context.ActingTenantId ?? Guid.Empty);
            var result = await lifecycle.RefundAsync(context.ActingUserId, originalId, $"{context.RunId:N}:{context.NodeId:N}",
                ct, context.ActingTenantId ?? Guid.Empty, cfg.Request.FeeAuthorization);
            if (result.IsError || result.Result is null) return ActionNodeResults.Fail(result.Message);
            if (result.Result.State != "Completed")
                return ActionNodeResults.Fail($"NFT refund {result.Result.OperationId} is {result.Result.State}; resume this lifecycle after participant authorization.");
            return ActionNodeResults.Ok(System.Text.Json.JsonSerializer.Serialize(result.Result), txHash: result.Result.TransactionId,
                chainType: result.Result.FeeQuote?.ChainType);
        }
        return ActionNodeResults.Invalid("Refund requires a succeeded transfer of this exact NFT earlier in the same run.");
    }
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

