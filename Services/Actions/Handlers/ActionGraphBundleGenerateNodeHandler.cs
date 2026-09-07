using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.ActionGraphBundleGenerate"/> — relocated verbatim from ActionManager.</summary>
public sealed class ActionGraphBundleGenerateNodeHandler : IActionNodeHandler
{
    private readonly IActionGraphBundleManager _bundleManager;

    public ActionGraphBundleGenerateNodeHandler(IActionGraphBundleManager bundleManager) => _bundleManager = bundleManager;

    public ActionNodeType NodeType => ActionNodeType.ActionGraphBundleGenerate;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<ActionGraphBundleGenerateNodeConfig>(context.Node.Config, nameof(ActionNodeType.ActionGraphBundleGenerate), out var cfg, out var cfgError))
            return ActionNodeResults.Fail(cfgError);
        var r = await _bundleManager.GenerateAsync(cfg.ActionGraphBundleId, cfg.Request, context.ActingUserId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
