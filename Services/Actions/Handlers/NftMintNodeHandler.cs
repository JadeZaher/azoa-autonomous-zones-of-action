using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.NftMint"/> — relocated verbatim from ActionManager.</summary>
public sealed class NftMintNodeHandler : IActionNodeHandler
{
    private readonly INftManager _nftManager;

    public NftMintNodeHandler(INftManager nftManager) => _nftManager = nftManager;

    public ActionNodeType NodeType => ActionNodeType.NftMint;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<NftMintRequest>(context.Node.Config, nameof(ActionNodeType.NftMint), out var model, out var cfgError))
            return ActionNodeResults.Fail(cfgError);
        var r = await _nftManager.MintAsync(model, context.ActingUserId, new AZOARequest { IdempotencyKey = $"{context.RunId:N}:{context.NodeId:N}" }, actingTenantId: context.ActingTenantId);
        var outputJson = ActionNodeOutputProjection.SerializeOperation(r);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
