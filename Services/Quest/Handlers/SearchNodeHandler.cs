using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>Handles <see cref="QuestNodeType.Search"/> — relocated verbatim from QuestManager.</summary>
public sealed class SearchNodeHandler : IQuestNodeHandler
{
    private readonly ISearchManager _searchManager;

    public SearchNodeHandler(ISearchManager searchManager) => _searchManager = searchManager;

    public QuestNodeType NodeType => QuestNodeType.Search;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!QuestNodeConfig.TryDeserialize<SearchRequest>(context.Node.Config, nameof(QuestNodeType.Search), out var searchReq, out var cfgError))
            return QuestNodeResults.Fail(cfgError);
        var r = await _searchManager.SearchAsync(searchReq, context.ActingAvatarId);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
