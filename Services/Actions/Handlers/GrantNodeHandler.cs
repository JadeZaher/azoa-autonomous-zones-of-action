using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>
/// Handles <see cref="ActionNodeType.Grant"/> — Tier-2 chain action (mint-to-actor).
/// Mechanism only: mints via <see cref="INftManager.MintAsync"/> with the actor
/// user taken from the run context (the config body carries no user). On a
/// successful mint, if the config opts in with a <see cref="GrantNodeConfig.HolonId"/>,
/// the linked holon's <c>token_id</c>/<c>chain_id</c> are populated from the mint
/// result (D10 Holon↔asset link). Requires a chain capability.
/// </summary>
public sealed class GrantNodeHandler : IActionNodeHandler
{
    private readonly INftManager _nftManager;
    private readonly IHolonManager _holonManager;

    public GrantNodeHandler(INftManager nftManager, IHolonManager holonManager)
    {
        _nftManager = nftManager;
        _holonManager = holonManager;
    }

    public ActionNodeType NodeType => ActionNodeType.Grant;

    public bool RequiresChainCapability => true;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<GrantNodeConfig>(context.Node.Config, nameof(ActionNodeType.Grant), out var cfg, out var cfgError))
            return ActionNodeResults.Fail(cfgError);

        // Actor is ALWAYS the run-context user; the config body user is ignored.
        // tenant-consent-delegation AC4: forward the run's acting tenant so a
        // tenant-driven grant stamps it on the op and the signing seam's consent
        // gate fires (null = user-driven → unchanged).
        var r = await _nftManager.MintAsync(cfg.Request, context.ActingUserId, actingTenantId: context.ActingTenantId);

        // The op can be non-null even on failure: a broadcast-then-confirmation-
        // timeout surfaces as IsError while the tx already landed. Read the hash
        // off the op regardless of outcome so the failure path forwards it to the
        // reconcile-before-retry engine (blockchain-recovery-and-portable-wallets §1.3)
        // — discarding it here is the double-mint hole this track closes.
        var opTxHash = ChainOperationOutputs.ReadTxHash(r.Result);
        var opChainType = ChainOperationOutputs.ReadChainType(r.Result);
        var outputJson = ActionNodeOutputProjection.SerializeOperation(r);

        // On failure, surface the hash so the engine reconciles (Confirmed→no-remint,
        // NotFound→retry, Indeterminate→park) instead of blind-retrying.
        if (r.IsError) return ActionNodeResults.Fail(r.Message, txHash: opTxHash, chainType: opChainType ?? "Algorand");

        // D10 Holon↔asset link (opt-in): copy the minted asset id + chain id onto
        // the tenant-named holon. Link is opt-in — skip when HolonId is null.
        if (cfg.HolonId is { } holonId && r.Result is { } operation)
        {
            var tokenId = ReadAssetId(operation);
            var chainId = ChainOperationOutputs.ReadChainType(operation);

            // Only update if at least one value is present, so a Pending mint with
            // no synchronous asset id doesn't clobber an existing holon value.
            if (tokenId is not null || chainId is not null)
            {
                var update = new HolonUpdateModel { TokenId = tokenId, ChainId = chainId };
                // C-1: scope the holon link to the runner (ActingUserId) so a marketplace run cannot touch the action owner's or a third party's holons.
                var link = await _holonManager.UpdateAsync(holonId, update, context.ActingUserId);
                if (link.IsError) return ActionNodeResults.Fail(link.Message);
            }
        }

        // Stamp the broadcast tx hash + chain so the reconcile-before-retry engine
        // can verify chain truth before any retry (blockchain-recovery-and-portable-wallets §1.3).
        // Only when the mint produced an operation (r.Result non-null) — a Pending
        // mint with no synchronous op carries no hash to reconcile against.
        return ActionNodeResults.Ok(outputJson, txHash: opTxHash, chainType: opChainType ?? "Algorand");
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

}
