using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.WalletSetDefault"/> — relocated verbatim from ActionManager.</summary>
public sealed class WalletSetDefaultNodeHandler : IActionNodeHandler
{
    private readonly IWalletManager _walletManager;

    public WalletSetDefaultNodeHandler(IWalletManager walletManager) => _walletManager = walletManager;

    public ActionNodeType NodeType => ActionNodeType.WalletSetDefault;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<WalletSetDefaultNodeConfig>(context.Node.Config, nameof(ActionNodeType.WalletSetDefault), out var cfg, out var cfgError))
            return ActionNodeResults.Invalid(cfgError);
        var r = await _walletManager.SetDefaultAsync(context.ActingUserId, cfg.WalletId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
