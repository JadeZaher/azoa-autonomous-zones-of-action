using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>
/// Handles <see cref="QuestNodeType.Refund"/> — Tier-2 chain action (D7).
/// Mechanically a reverse <see cref="INftManager.TransferAsync"/>: the
/// <see cref="RefundNodeConfig.Request"/> already carries the reversed direction
/// (recipient = original sender), set by the saga/tenant. Kept distinct from
/// <see cref="QuestNodeType.Transfer"/> by node type so the Track-2 saga can
/// declare "compensation = the Refund node". Actor is taken from the run context.
/// <para>
/// Soulbound assets fail closed: a true clawback of a non-transferable asset
/// needs a clawback primitive (deferred to H2 / signing D7), so the reversal is
/// refused with a clear message rather than silently no-op'd.
/// </para>
/// Requires a chain capability.
/// </summary>
public sealed class RefundNodeHandler : IQuestNodeHandler
{
    private const string ClawbackDeferredMessage =
        "soulbound reversal requires clawback primitive — deferred (H2 / signing D7)";

    private readonly INftManager _nftManager;

    public RefundNodeHandler(INftManager nftManager) => _nftManager = nftManager;

    public QuestNodeType NodeType => QuestNodeType.Refund;

    public bool RequiresChainCapability => true;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        var cfg = JsonSerializer.Deserialize<RefundNodeConfig>(context.Node.Config, QuestNodeJson.Options)!;

        // Soulbound detection: the NFT is a Holon (AssetType="NFT") and the
        // Holon-based INftManager.TransferAsync has no soulbound concept (it never
        // returns a soulbound error — that flag lives only on AvatarNFT). So we
        // read the asset and inspect its metadata for a truthy soulbound marker.
        // If set, fail closed BEFORE attempting any transfer (clawback deferred).
        var nft = await _nftManager.GetAsync(cfg.NftId);
        if (nft.IsError) return QuestNodeResults.Fail(nft.Message);
        if (nft.Result is { } asset && IsSoulbound(asset.Metadata))
            return QuestNodeResults.Fail(ClawbackDeferredMessage);

        // Actor is ALWAYS the run-context avatar; the config body avatar is ignored.
        // tenant-consent-delegation AC4: forward the run's acting tenant so a
        // tenant-driven refund (reverse transfer) stamps it on the op for the
        // seam's consent gate.
        var r = await _nftManager.TransferAsync(cfg.NftId, cfg.Request, context.Quest.AvatarId, actingTenantId: context.ActingTenantId);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);

        // Forward the broadcast tx hash on BOTH outcomes (a refund is a reverse
        // transfer): a broadcast-then-confirmation-timeout surfaces as IsError while
        // the reversal already landed, so the reconcile-before-retry engine must
        // verify chain truth instead of blind-retrying
        // (blockchain-recovery-and-portable-wallets §1.3).
        var opTxHash = ChainOperationOutputs.ReadTxHash(r.Result);
        var opChainType = ChainOperationOutputs.ReadChainType(r.Result);

        if (r.IsError) return QuestNodeResults.Fail(r.Message, txHash: opTxHash, chainType: opChainType ?? "Algorand");
        return QuestNodeResults.Ok(outputJson, txHash: opTxHash, chainType: opChainType ?? "Algorand");
    }

    private static bool IsSoulbound(Dictionary<string, string> metadata)
    {
        foreach (var key in new[] { "soulbound", "isSoulbound", "is_soulbound" })
            if (metadata.TryGetValue(key, out var v) &&
                bool.TryParse(v, out var b) && b)
                return true;
        return false;
    }
}
