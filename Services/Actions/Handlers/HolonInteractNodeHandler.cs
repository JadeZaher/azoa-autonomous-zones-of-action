using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.HolonInteract"/> — relocated verbatim from ActionManager.</summary>
public sealed class HolonInteractNodeHandler : IActionNodeHandler
{
    private readonly IHolonManager _holonManager;

    public HolonInteractNodeHandler(IHolonManager holonManager) => _holonManager = holonManager;

    public ActionNodeType NodeType => ActionNodeType.HolonInteract;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<HolonInteractNodeConfig>(context.Node.Config, nameof(ActionNodeType.HolonInteract), out var cfg, out var cfgError))
            return ActionNodeResults.Invalid(cfgError);
        // C-2: scope to the runner (ActingUserId) so a marketplace run cannot mutate a victim's holon by GUID.
        var r = await _holonManager.InteractAsync(cfg.HolonId, cfg.Request, context.ActingUserId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
