using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.HolonQuery"/> — relocated verbatim from ActionManager.</summary>
public sealed class HolonQueryNodeHandler : IActionNodeHandler
{
    private readonly IHolonManager _holonManager;

    public HolonQueryNodeHandler(IHolonManager holonManager) => _holonManager = holonManager;

    public ActionNodeType NodeType => ActionNodeType.HolonQuery;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<HolonQueryRequest>(context.Node.Config, nameof(ActionNodeType.HolonQuery), out var query, out var cfgError))
            return ActionNodeResults.Fail(cfgError);
        var r = await _holonManager.QueryAsync(query, context.ActingUserId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
