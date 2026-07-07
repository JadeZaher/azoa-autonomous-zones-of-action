using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>Handles <see cref="QuestNodeType.WalletSetDefault"/> — relocated verbatim from QuestManager.</summary>
public sealed class WalletSetDefaultNodeHandler : IQuestNodeHandler
{
    private readonly IWalletManager _walletManager;

    public WalletSetDefaultNodeHandler(IWalletManager walletManager) => _walletManager = walletManager;

    public QuestNodeType NodeType => QuestNodeType.WalletSetDefault;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!QuestNodeConfig.TryDeserialize<WalletSetDefaultNodeConfig>(context.Node.Config, nameof(QuestNodeType.WalletSetDefault), out var cfg, out var cfgError))
            return QuestNodeResults.Fail(cfgError);
        var r = await _walletManager.SetDefaultAsync(context.ActingAvatarId, cfg.WalletId);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
