using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>Handles <see cref="QuestNodeType.NftBurn"/> — relocated verbatim from QuestManager.</summary>
public sealed class NftBurnNodeHandler : IQuestNodeHandler
{
    private readonly INftManager _nftManager;

    public NftBurnNodeHandler(INftManager nftManager) => _nftManager = nftManager;

    public QuestNodeType NodeType => QuestNodeType.NftBurn;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!QuestNodeConfig.TryDeserialize<NftBurnNodeConfig>(context.Node.Config, nameof(QuestNodeType.NftBurn), out var cfg, out var cfgError))
            return QuestNodeResults.Fail(cfgError);
        var r = await _nftManager.BurnAsync(cfg.NftId, cfg.WalletId, context.ActingAvatarId);
        var outputJson = QuestNodeOutputProjection.SerializeOperation(r);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
