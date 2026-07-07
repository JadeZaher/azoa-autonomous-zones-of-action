using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>
/// Handles <see cref="QuestNodeType.FungibleTokenCreate"/> — Tier-2 chain action
/// (launch a fungible ASA with a real supply + decimals, the parallel to the
/// supply-1 mint path). Routes through <see cref="IFungibleTokenManager.CreateAsync"/>
/// with the actor avatar taken from the run context (the config body carries no
/// avatar — Grant precedent). On a successful launch, if the config opts in with a
/// <see cref="FungibleTokenCreateNodeConfig.HolonId"/>, the linked holon's
/// <c>token_id</c>/<c>chain_id</c> are populated from the created asset id (D10
/// Holon↔asset link). Requires a chain capability.
/// </summary>
public sealed class FungibleTokenCreateNodeHandler : IQuestNodeHandler
{
    private readonly IFungibleTokenManager _fungibleTokenManager;
    private readonly IHolonManager _holonManager;

    public FungibleTokenCreateNodeHandler(
        IFungibleTokenManager fungibleTokenManager, IHolonManager holonManager)
    {
        _fungibleTokenManager = fungibleTokenManager;
        _holonManager = holonManager;
    }

    public QuestNodeType NodeType => QuestNodeType.FungibleTokenCreate;

    public bool RequiresChainCapability => true;

    public async Task<QuestNodeHandlerResult> HandleAsync(
        QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!QuestNodeConfig.TryDeserialize<FungibleTokenCreateNodeConfig>(context.Node.Config, nameof(QuestNodeType.FungibleTokenCreate), out var cfg, out var cfgError))
            return QuestNodeResults.Fail(cfgError);

        var request = new FungibleTokenCreateRequest
        {
            Name = cfg.Name,
            UnitName = cfg.UnitName,
            ChainType = cfg.ChainType,
            Total = cfg.Total,
            Decimals = cfg.Decimals
        };

        // Idempotency context: QuestNodeExecutionContext exposes no API-key /
        // client-idempotency surface, so derive a STABLE key from the run+node
        // identity (SwapNodeHandler precedent). It is unique per (run, node) so a
        // re-evaluation of the same node dedupes; the manager partitions by the
        // apiKeyId argument, so we pass the run id as a stable sentinel partition.
        var apiKeyId = context.RunId.ToString();
        var clientIdempotencyKey = $"{context.RunId}:{context.NodeId}";

        // Actor is ALWAYS the acting run avatar (the RUNNER); the config body avatar
        // is ignored. C1: on a marketplace run this is the runner, not the quest
        // owner, so the ASA is created + owned under the runner's identity.
        // tenant-consent-delegation AC4: forward the run's acting tenant so a
        // tenant-driven ASA create builds the tenant-driven SigningContext for the
        // platform-signed create and the seam's live consent gate fires
        // (null = user-driven → unchanged).
        var r = await _fungibleTokenManager.CreateAsync(
            context.ActingAvatarId, request, context.ActingAvatarId,
            clientIdempotencyKey, apiKeyId, context.ActingTenantId);

        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError || r.Result is null) return QuestNodeResults.Fail(r.Message);

        // D10 Holon↔asset link (opt-in): copy the created asset id + chain id onto
        // the tenant-named holon. Link is opt-in — skip when HolonId is null.
        if (cfg.HolonId is { } holonId)
        {
            var tokenId = string.IsNullOrWhiteSpace(r.Result.AssetId) ? null : r.Result.AssetId;
            var chainId = string.IsNullOrWhiteSpace(cfg.ChainType) ? null : cfg.ChainType;

            if (tokenId is not null || chainId is not null)
            {
                var update = new HolonUpdateModel { TokenId = tokenId, ChainId = chainId };
                // Trusted internal path (no avatar scoping) — quest node handlers run
                // unscoped, matching GrantNodeHandler / HolonUpdateNodeHandler.
                var link = await _holonManager.UpdateAsync(holonId, update);
                if (link.IsError) return QuestNodeResults.Fail(link.Message);
            }
        }

        return QuestNodeResults.Ok(outputJson);
    }
}
