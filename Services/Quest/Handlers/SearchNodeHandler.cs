using System.Text.Json;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Interfaces.QuestExecution;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Services.Quest.Handlers;

/// <summary>Handles <see cref="QuestNodeType.Search"/> — relocated verbatim from QuestManager.</summary>
public sealed class SearchNodeHandler : IQuestNodeHandler
{
    private readonly ISearchManager _searchManager;

    public SearchNodeHandler(ISearchManager searchManager) => _searchManager = searchManager;

    public QuestNodeType NodeType => QuestNodeType.Search;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        var searchReq = JsonSerializer.Deserialize<SearchRequest>(context.Node.Config, QuestNodeJson.Options)!;
        var r = await _searchManager.SearchAsync(searchReq);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
