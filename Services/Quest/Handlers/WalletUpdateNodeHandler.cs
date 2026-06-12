using System.Text.Json;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Interfaces.QuestExecution;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Services.Quest.Handlers;

/// <summary>Handles <see cref="QuestNodeType.WalletUpdate"/> — relocated verbatim from QuestManager.</summary>
public sealed class WalletUpdateNodeHandler : IQuestNodeHandler
{
    private readonly IWalletManager _walletManager;

    public WalletUpdateNodeHandler(IWalletManager walletManager) => _walletManager = walletManager;

    public QuestNodeType NodeType => QuestNodeType.WalletUpdate;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        var cfg = JsonSerializer.Deserialize<WalletUpdateNodeConfig>(context.Node.Config, QuestNodeJson.Options)!;
        var r = await _walletManager.UpdateAsync(cfg.WalletId, cfg.Model, context.Quest.AvatarId);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
