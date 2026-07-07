using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>Handles <see cref="QuestNodeType.NftQuery"/> — relocated verbatim from QuestManager.</summary>
public sealed class NftQueryNodeHandler : IQuestNodeHandler
{
    private readonly INftManager _nftManager;

    public NftQueryNodeHandler(INftManager nftManager) => _nftManager = nftManager;

    public QuestNodeType NodeType => QuestNodeType.NftQuery;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!QuestNodeConfig.TryDeserialize<NftQueryRequest>(context.Node.Config, nameof(QuestNodeType.NftQuery), out var query, out var cfgError))
            return QuestNodeResults.Fail(cfgError);
        var r = await _nftManager.QueryAsync(query, context.ActingAvatarId);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
