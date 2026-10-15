using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.WalletQuery"/> — relocated verbatim from ActionManager.</summary>
public sealed class WalletQueryNodeHandler : IActionNodeHandler
{
    private readonly IWalletManager _walletManager;

    public WalletQueryNodeHandler(IWalletManager walletManager) => _walletManager = walletManager;

    public ActionNodeType NodeType => ActionNodeType.WalletQuery;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<WalletQueryRequest>(context.Node.Config, nameof(ActionNodeType.WalletQuery), out var query, out var cfgError))
            return ActionNodeResults.Fail(cfgError);
        var r = await _walletManager.QueryAsync(query, context.ActingUserId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
