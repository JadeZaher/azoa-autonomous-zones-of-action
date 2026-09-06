using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.HolonMoveSubtree"/> — relocated verbatim from ActionManager.</summary>
public sealed class HolonMoveSubtreeNodeHandler : IActionNodeHandler
{
    private readonly IHolonManager _holonManager;

    public HolonMoveSubtreeNodeHandler(IHolonManager holonManager) => _holonManager = holonManager;

    public ActionNodeType NodeType => ActionNodeType.HolonMoveSubtree;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<HolonMoveNodeConfig>(context.Node.Config, nameof(ActionNodeType.HolonMoveSubtree), out var cfg, out var cfgError))
            return ActionNodeResults.Fail(cfgError);
        // C-2: scope to the runner (ActingUserId) so a marketplace run cannot move a victim's holon by GUID.
        var r = await _holonManager.MoveSubtreeAsync(cfg.HolonId, cfg.NewParentId, context.ActingUserId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
