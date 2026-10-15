using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.WalletCreate"/> — relocated verbatim from ActionManager.</summary>
public sealed class WalletCreateNodeHandler : IActionNodeHandler
{
    private readonly IWalletManager _walletManager;

    public WalletCreateNodeHandler(IWalletManager walletManager) => _walletManager = walletManager;

    public ActionNodeType NodeType => ActionNodeType.WalletCreate;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<WalletCreateModel>(context.Node.Config, nameof(ActionNodeType.WalletCreate), out var model, out var cfgError))
            return ActionNodeResults.Fail(cfgError);
        var r = await _walletManager.CreateAsync(model, context.ActingUserId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
