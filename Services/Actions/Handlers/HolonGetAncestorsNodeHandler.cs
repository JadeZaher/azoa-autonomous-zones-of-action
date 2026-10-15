using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.HolonGetAncestors"/> — relocated verbatim from ActionManager.</summary>
public sealed class HolonGetAncestorsNodeHandler : IActionNodeHandler
{
    private readonly IHolonManager _holonManager;

    public HolonGetAncestorsNodeHandler(IHolonManager holonManager) => _holonManager = holonManager;

    public ActionNodeType NodeType => ActionNodeType.HolonGetAncestors;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<IdConfig>(context.Node.Config, nameof(ActionNodeType.HolonGetAncestors), out var cfg, out var cfgError))
            return ActionNodeResults.Fail(cfgError);
        var r = await _holonManager.GetAncestorsAsync(cfg.Id, context.ActingUserId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
