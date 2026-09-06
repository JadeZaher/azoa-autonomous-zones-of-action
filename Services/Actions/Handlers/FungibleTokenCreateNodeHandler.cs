using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>
/// Handles <see cref="ActionNodeType.FungibleTokenCreate"/> — Tier-2 chain action
/// (launch a fungible ASA with a real supply + decimals, the parallel to the
/// supply-1 mint path). Routes through <see cref="IFungibleTokenManager.CreateAsync"/>
/// with the actor user taken from the run context (the config body carries no
/// user — Grant precedent). On a successful launch, if the config opts in with a
/// <see cref="FungibleTokenCreateNodeConfig.HolonId"/>, the linked holon's
/// <c>token_id</c>/<c>chain_id</c> are populated from the created asset id (D10
/// Holon↔asset link). Requires a chain capability.
/// </summary>
public sealed class FungibleTokenCreateNodeHandler : IActionNodeHandler
{
    private readonly IFungibleTokenManager _fungibleTokenManager;
    private readonly IHolonManager _holonManager;

    public FungibleTokenCreateNodeHandler(
        IFungibleTokenManager fungibleTokenManager, IHolonManager holonManager)
    {
        _fungibleTokenManager = fungibleTokenManager;
        _holonManager = holonManager;
    }

    public ActionNodeType NodeType => ActionNodeType.FungibleTokenCreate;

    public bool RequiresChainCapability => true;

    /// <summary>This node launches its asset on the chain its config names, not the default one.</summary>
    public string? ResolveSettlementRailId(ActionNode node) =>
        ActionNodeConfig.TryDeserialize<FungibleTokenCreateNodeConfig>(
            node.Config, nameof(ActionNodeType.FungibleTokenCreate), out var cfg, out _)
            ? cfg.ChainType
            : null;

    public async Task<ActionNodeHandlerResult> HandleAsync(
        ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<FungibleTokenCreateNodeConfig>(context.Node.Config, nameof(ActionNodeType.FungibleTokenCreate), out var cfg, out var cfgError))
            return ActionNodeResults.Fail(cfgError);

        var request = new FungibleTokenCreateRequest
        {
            Name = cfg.Name,
            UnitName = cfg.UnitName,
            ChainType = cfg.ChainType,
            Total = cfg.Total,
            Decimals = cfg.Decimals
        };

        // Idempotency context: ActionNodeExecutionContext exposes no API-key /
        // client-idempotency surface, so derive a STABLE key from the run+node
        // identity (SwapNodeHandler precedent). It is unique per (run, node) so a
        // re-evaluation of the same node dedupes; the manager partitions by the
        // apiKeyId argument, so we pass the run id as a stable sentinel partition.
        var apiKeyId = context.RunId.ToString();
        var clientIdempotencyKey = $"{context.RunId}:{context.NodeId}";

        // Actor is ALWAYS the acting run user (the RUNNER); the config body user
        // is ignored. C1: on a marketplace run this is the runner, not the action
        // owner, so the ASA is created + owned under the runner's identity.
        // tenant-consent-delegation AC4: forward the run's acting tenant so a
        // tenant-driven ASA create builds the tenant-driven SigningContext for the
        // platform-signed create and the seam's live consent gate fires
        // (null = user-driven → unchanged).
        var r = await _fungibleTokenManager.CreateAsync(
            context.ActingUserId, request, context.ActingUserId,
            clientIdempotencyKey, apiKeyId, context.ActingTenantId);

        var outputJson = ActionNodeOutputProjection.SerializeFungible(r);
        if (r.IsError || r.Result is null) return ActionNodeResults.Fail(r.Message);

        // D10 Holon↔asset link (opt-in): copy the created asset id + chain id onto
        // the tenant-named holon. Link is opt-in — skip when HolonId is null.
        if (cfg.HolonId is { } holonId)
        {
            var tokenId = string.IsNullOrWhiteSpace(r.Result.AssetId) ? null : r.Result.AssetId;
            var chainId = string.IsNullOrWhiteSpace(cfg.ChainType) ? null : cfg.ChainType;

            if (tokenId is not null || chainId is not null)
            {
                var update = new HolonUpdateModel { TokenId = tokenId, ChainId = chainId };
                // C-1: scope the holon link to the runner (ActingUserId) so a marketplace run cannot touch the action owner's or a third party's holons.
                var link = await _holonManager.UpdateAsync(holonId, update, context.ActingUserId);
                if (link.IsError) return ActionNodeResults.Fail(link.Message);
            }
        }

        return ActionNodeResults.Ok(outputJson);
    }
}
