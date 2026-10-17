using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.HolonGetDescendants"/> — relocated verbatim from ActionManager.</summary>
public sealed class HolonGetDescendantsNodeHandler : IActionNodeHandler
{
    private readonly IHolonManager _holonManager;

    public HolonGetDescendantsNodeHandler(IHolonManager holonManager) => _holonManager = holonManager;

    public ActionNodeType NodeType => ActionNodeType.HolonGetDescendants;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<IdConfig>(context.Node.Config, nameof(ActionNodeType.HolonGetDescendants), out var cfg, out var cfgError))
            return ActionNodeResults.Fail(cfgError);
        var r = await _holonManager.GetDescendantsAsync(cfg.Id, context.ActingUserId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
