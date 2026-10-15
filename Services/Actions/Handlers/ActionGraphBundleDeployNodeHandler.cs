using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.ActionGraphBundleDeploy"/> — relocated verbatim from ActionManager.</summary>
public sealed class ActionGraphBundleDeployNodeHandler : IActionNodeHandler
{
    private readonly IActionGraphBundleManager _bundleManager;

    public ActionGraphBundleDeployNodeHandler(IActionGraphBundleManager bundleManager) => _bundleManager = bundleManager;

    public ActionNodeType NodeType => ActionNodeType.ActionGraphBundleDeploy;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<IdConfig>(context.Node.Config, nameof(ActionNodeType.ActionGraphBundleDeploy), out var cfg, out var cfgError))
            return ActionNodeResults.Fail(cfgError);
        var r = await _bundleManager.DeployAsync(cfg.Id, context.ActingUserId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
