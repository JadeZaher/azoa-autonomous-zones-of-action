using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.WalletDelete"/> — relocated verbatim from ActionManager.</summary>
public sealed class WalletDeleteNodeHandler : IActionNodeHandler
{
    private readonly IWalletManager _walletManager;

    public WalletDeleteNodeHandler(IWalletManager walletManager) => _walletManager = walletManager;

    public ActionNodeType NodeType => ActionNodeType.WalletDelete;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<IdConfig>(context.Node.Config, nameof(ActionNodeType.WalletDelete), out var cfg, out var cfgError))
            return ActionNodeResults.Invalid(cfgError);
        var r = await _walletManager.DeleteAsync(cfg.Id, context.ActingUserId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
