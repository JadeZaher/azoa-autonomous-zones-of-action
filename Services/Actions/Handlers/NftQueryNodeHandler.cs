using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.NftQuery"/> — relocated verbatim from ActionManager.</summary>
public sealed class NftQueryNodeHandler : IActionNodeHandler
{
    private readonly INftManager _nftManager;

    public NftQueryNodeHandler(INftManager nftManager) => _nftManager = nftManager;

    public ActionNodeType NodeType => ActionNodeType.NftQuery;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<NftQueryRequest>(context.Node.Config, nameof(ActionNodeType.NftQuery), out var query, out var cfgError))
            return ActionNodeResults.Fail(cfgError);
        var r = await _nftManager.QueryAsync(query, context.ActingUserId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
