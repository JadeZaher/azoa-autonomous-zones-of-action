using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>Handles <see cref="QuestNodeType.WalletDelete"/> — relocated verbatim from QuestManager.</summary>
public sealed class WalletDeleteNodeHandler : IQuestNodeHandler
{
    private readonly IWalletManager _walletManager;

    public WalletDeleteNodeHandler(IWalletManager walletManager) => _walletManager = walletManager;

    public QuestNodeType NodeType => QuestNodeType.WalletDelete;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!QuestNodeConfig.TryDeserialize<IdConfig>(context.Node.Config, nameof(QuestNodeType.WalletDelete), out var cfg, out var cfgError))
            return QuestNodeResults.Fail(cfgError);
        var r = await _walletManager.DeleteAsync(cfg.Id, context.ActingAvatarId);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
