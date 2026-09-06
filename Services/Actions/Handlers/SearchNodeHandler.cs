using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.Search"/> — relocated verbatim from ActionManager.</summary>
public sealed class SearchNodeHandler : IActionNodeHandler
{
    private readonly ISearchManager _searchManager;

    public SearchNodeHandler(ISearchManager searchManager) => _searchManager = searchManager;

    public ActionNodeType NodeType => ActionNodeType.Search;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<SearchRequest>(context.Node.Config, nameof(ActionNodeType.Search), out var searchReq, out var cfgError))
            return ActionNodeResults.Fail(cfgError);
        var r = await _searchManager.SearchAsync(searchReq, context.ActingUserId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
