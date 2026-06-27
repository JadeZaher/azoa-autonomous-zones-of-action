using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>
/// Handles <see cref="QuestNodeType.Transfer"/> — Tier-2 chain action (move-to-actor).
/// Mechanism only: wraps <see cref="INftManager.TransferAsync"/> with the actor
/// avatar taken from the run context (the config body carries no avatar). Distinct
/// from the legacy <see cref="QuestNodeType.NftTransfer"/> handler: this is the
/// capability-gated Tier-2 node. Requires a chain capability.
/// </summary>
public sealed class TransferNodeHandler : IQuestNodeHandler
{
    private readonly INftManager _nftManager;

    public TransferNodeHandler(INftManager nftManager) => _nftManager = nftManager;

    public QuestNodeType NodeType => QuestNodeType.Transfer;

    public bool RequiresChainCapability => true;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        var cfg = JsonSerializer.Deserialize<TransferNodeConfig>(context.Node.Config, QuestNodeJson.Options)!;
        // tenant-consent-delegation AC4: forward the run's acting tenant so a
        // tenant-driven transfer stamps it on the op for the seam's consent gate.
        var r = await _nftManager.TransferAsync(cfg.NftId, cfg.Request, context.Quest.AvatarId, actingTenantId: context.ActingTenantId);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);

        // Forward the broadcast tx hash on BOTH outcomes: a transfer that broadcast
        // then timed out on confirmation surfaces as IsError while the move already
        // landed. The reconcile-before-retry engine needs the hash to verify chain
        // truth instead of blind-retrying (blockchain-recovery-and-portable-wallets §1.3).
        var opTxHash = ChainOperationOutputs.ReadTxHash(r.Result);
        var opChainType = ChainOperationOutputs.ReadChainType(r.Result);

        if (r.IsError) return QuestNodeResults.Fail(r.Message, txHash: opTxHash, chainType: opChainType ?? "Algorand");
        return QuestNodeResults.Ok(outputJson, txHash: opTxHash, chainType: opChainType ?? "Algorand");
    }
}
