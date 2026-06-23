using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>
/// Handles <see cref="QuestNodeType.Grant"/> — Tier-2 chain action (mint-to-actor).
/// Mechanism only: mints via <see cref="INftManager.MintAsync"/> with the actor
/// avatar taken from the run context (the config body carries no avatar). On a
/// successful mint, if the config opts in with a <see cref="GrantNodeConfig.HolonId"/>,
/// the linked holon's <c>token_id</c>/<c>chain_id</c> are populated from the mint
/// result (D10 Holon↔asset link). Requires a chain capability.
/// </summary>
public sealed class GrantNodeHandler : IQuestNodeHandler
{
    private readonly INftManager _nftManager;
    private readonly IHolonManager _holonManager;

    public GrantNodeHandler(INftManager nftManager, IHolonManager holonManager)
    {
        _nftManager = nftManager;
        _holonManager = holonManager;
    }

    public QuestNodeType NodeType => QuestNodeType.Grant;

    public bool RequiresChainCapability => true;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        var cfg = JsonSerializer.Deserialize<GrantNodeConfig>(context.Node.Config, QuestNodeJson.Options)!;

        // Actor is ALWAYS the run-context avatar; the config body avatar is ignored.
        // tenant-consent-delegation AC4: forward the run's acting tenant so a
        // tenant-driven grant stamps it on the op and the signing seam's consent
        // gate fires (null = user-driven → unchanged).
        var r = await _nftManager.MintAsync(cfg.Request, context.Quest.AvatarId, actingTenantId: context.ActingTenantId);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);

        // D10 Holon↔asset link (opt-in): copy the minted asset id + chain id onto
        // the tenant-named holon. Link is opt-in — skip when HolonId is null.
        if (cfg.HolonId is { } holonId && r.Result is { } operation)
        {
            var tokenId = ReadAssetId(operation);
            var chainId = ReadChainId(operation);

            // Only update if at least one value is present, so a Pending mint with
            // no synchronous asset id doesn't clobber an existing holon value.
            if (tokenId is not null || chainId is not null)
            {
                var update = new HolonUpdateModel { TokenId = tokenId, ChainId = chainId };
                // Trusted internal path (no avatar scoping) — quest node handlers run
                // unscoped, matching HolonUpdateNodeHandler's call shape.
                var link = await _holonManager.UpdateAsync(holonId, update);
                if (link.IsError) return QuestNodeResults.Fail(link.Message);
            }
        }

        return QuestNodeResults.Ok(outputJson);
    }

    // The IBlockchainOperation has no typed asset_id/tx_hash property — the minted
    // asset id surfaces via the Parameters bag, whose key varies by provider/manager
    // (the NftManager mint path stamps "chainId"/"holonId"; Algorand stamps
    // "assetId"). Read several candidate keys defensively and fall back to null.
    private static string? ReadAssetId(IBlockchainOperation op)
    {
        foreach (var key in new[] { "assetId", "asset_id", "tokenId", "token_id" })
            if (op.Parameters.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }

    private static string? ReadChainId(IBlockchainOperation op)
    {
        foreach (var key in new[] { "chainId", "chain_id" })
            if (op.Parameters.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }
}
