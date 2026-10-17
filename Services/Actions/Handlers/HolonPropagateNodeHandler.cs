using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.HolonPropagate"/> — relocated verbatim from ActionManager.</summary>
public sealed class HolonPropagateNodeHandler : IActionNodeHandler
{
    private readonly IHolonManager _holonManager;

    public HolonPropagateNodeHandler(IHolonManager holonManager) => _holonManager = holonManager;

    public ActionNodeType NodeType => ActionNodeType.HolonPropagate;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<HolonPropagateNodeConfig>(context.Node.Config, nameof(ActionNodeType.HolonPropagate), out var cfg, out var cfgError))
            return ActionNodeResults.Invalid(cfgError);
        // C-2: scope to the runner (ActingUserId) so a marketplace run cannot propagate over a victim's holon by GUID.
        var r = await _holonManager.PropagateAsync(cfg.HolonId, cfg.Request, context.ActingUserId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
