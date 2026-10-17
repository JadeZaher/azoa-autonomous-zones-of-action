using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.HolonDelete"/> — relocated verbatim from ActionManager.</summary>
public sealed class HolonDeleteNodeHandler : IActionNodeHandler
{
    private readonly IHolonManager _holonManager;

    public HolonDeleteNodeHandler(IHolonManager holonManager) => _holonManager = holonManager;

    public ActionNodeType NodeType => ActionNodeType.HolonDelete;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<IdConfig>(context.Node.Config, nameof(ActionNodeType.HolonDelete), out var cfg, out var cfgError))
            return ActionNodeResults.Invalid(cfgError);
        // C1: scope the delete to the acting user (the RUNNER) so a marketplace
        // run can only delete the runner's own holons — never the action owner's.
        var r = await _holonManager.DeleteAsync(cfg.Id, context.ActingUserId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
