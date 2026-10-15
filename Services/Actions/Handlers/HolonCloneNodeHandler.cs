using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.HolonClone"/> — relocated verbatim from ActionManager.</summary>
public sealed class HolonCloneNodeHandler : IActionNodeHandler
{
    private readonly IHolonManager _holonManager;

    public HolonCloneNodeHandler(IHolonManager holonManager) => _holonManager = holonManager;

    public ActionNodeType NodeType => ActionNodeType.HolonClone;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<HolonCloneNodeConfig>(context.Node.Config, nameof(ActionNodeType.HolonClone), out var cfg, out var cfgError))
            return ActionNodeResults.Fail(cfgError);
        var r = await _holonManager.CloneAsync(cfg.HolonId, cfg.Request, context.ActingUserId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
