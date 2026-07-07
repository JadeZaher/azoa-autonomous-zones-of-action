using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>Handles <see cref="QuestNodeType.NftTransfer"/> — relocated verbatim from QuestManager.</summary>
public sealed class NftTransferNodeHandler : IQuestNodeHandler
{
    private readonly INftManager _nftManager;

    public NftTransferNodeHandler(INftManager nftManager) => _nftManager = nftManager;

    public QuestNodeType NodeType => QuestNodeType.NftTransfer;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!QuestNodeConfig.TryDeserialize<NftTransferNodeConfig>(context.Node.Config, nameof(QuestNodeType.NftTransfer), out var cfg, out var cfgError))
            return QuestNodeResults.Fail(cfgError);
        var r = await _nftManager.TransferAsync(cfg.NftId, cfg.Request, context.ActingAvatarId);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
