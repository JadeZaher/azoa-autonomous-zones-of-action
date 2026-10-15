using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.NftBurn"/> — relocated verbatim from ActionManager.</summary>
public sealed class NftBurnNodeHandler : IActionNodeHandler
{
    private readonly INftManager _nftManager;

    public NftBurnNodeHandler(INftManager nftManager) => _nftManager = nftManager;

    public ActionNodeType NodeType => ActionNodeType.NftBurn;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<NftBurnNodeConfig>(context.Node.Config, nameof(ActionNodeType.NftBurn), out var cfg, out var cfgError))
            return ActionNodeResults.Fail(cfgError);
        var r = await _nftManager.BurnAsync(cfg.NftId, cfg.WalletId, context.ActingUserId);
        var outputJson = ActionNodeOutputProjection.SerializeOperation(r);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
