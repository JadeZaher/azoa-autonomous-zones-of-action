using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.WalletUpdate"/> — relocated verbatim from ActionManager.</summary>
public sealed class WalletUpdateNodeHandler : IActionNodeHandler
{
    private readonly IWalletManager _walletManager;

    public WalletUpdateNodeHandler(IWalletManager walletManager) => _walletManager = walletManager;

    public ActionNodeType NodeType => ActionNodeType.WalletUpdate;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<WalletUpdateNodeConfig>(context.Node.Config, nameof(ActionNodeType.WalletUpdate), out var cfg, out var cfgError))
            return ActionNodeResults.Invalid(cfgError);
        var r = await _walletManager.UpdateAsync(cfg.WalletId, cfg.Model, context.ActingUserId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
