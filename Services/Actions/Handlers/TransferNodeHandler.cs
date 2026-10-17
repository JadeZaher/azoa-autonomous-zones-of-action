using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>
/// Handles <see cref="ActionNodeType.Transfer"/> — Tier-2 chain action (move-to-actor).
/// Mechanism only: wraps <see cref="INftManager.TransferAsync"/> with the actor
/// user taken from the run context (the config body carries no user). Distinct
/// from the legacy <see cref="ActionNodeType.NftTransfer"/> handler: this is the
/// capability-gated Tier-2 node. Requires a chain capability.
/// </summary>
public sealed class TransferNodeHandler : IActionNodeHandler
{
    private readonly INftManager _nftManager;

    public TransferNodeHandler(INftManager nftManager) => _nftManager = nftManager;

    public ActionNodeType NodeType => ActionNodeType.Transfer;

    public bool RequiresChainCapability => true;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<TransferNodeConfig>(context.Node.Config, nameof(ActionNodeType.Transfer), out var cfg, out var cfgError))
            return ActionNodeResults.Fail(cfgError);
        // tenant-consent-delegation AC4: forward the run's acting tenant so a
        // tenant-driven transfer stamps it on the op for the seam's consent gate.
        var r = await _nftManager.TransferAsync(cfg.NftId, cfg.Request, context.ActingUserId, new AZOARequest { IdempotencyKey = $"{context.RunId:N}:{context.NodeId:N}" }, actingTenantId: context.ActingTenantId);

        // Forward the broadcast tx hash on BOTH outcomes: a transfer that broadcast
        // then timed out on confirmation surfaces as IsError while the move already
        // landed. The reconcile-before-retry engine needs the hash to verify chain
        // truth instead of blind-retrying (blockchain-recovery-and-portable-wallets §1.3).
        var opTxHash = ChainOperationOutputs.ReadTxHash(r.Result);
        var opChainType = ChainOperationOutputs.ReadChainType(r.Result);
        var outputJson = ActionNodeOutputProjection.SerializeOperation(r);

        if (r.IsError) return ActionNodeResults.Fail(r.Message, txHash: opTxHash, chainType: opChainType ?? "Algorand");
        return ActionNodeResults.Ok(outputJson, txHash: opTxHash, chainType: opChainType ?? "Algorand");
    }
}
