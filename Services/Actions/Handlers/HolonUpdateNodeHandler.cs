using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.HolonUpdate"/> — relocated verbatim from ActionManager.</summary>
public sealed class HolonUpdateNodeHandler : IActionNodeHandler
{
    private readonly IHolonManager _holonManager;

    public HolonUpdateNodeHandler(IHolonManager holonManager) => _holonManager = holonManager;

    public ActionNodeType NodeType => ActionNodeType.HolonUpdate;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<HolonUpdateNodeConfig>(context.Node.Config, nameof(ActionNodeType.HolonUpdate), out var cfg, out var cfgError))
            return ActionNodeResults.Fail(cfgError);
        // C1: scope the update to the acting user (the RUNNER) so a marketplace
        // run can only mutate the runner's own holons — never the action owner's.
        var r = await _holonManager.UpdateAsync(cfg.HolonId, cfg.Model, context.ActingUserId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
