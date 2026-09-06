using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.StarDeploy"/> — relocated verbatim from ActionManager.</summary>
public sealed class StarDeployNodeHandler : IActionNodeHandler
{
    private readonly ISTARManager _starManager;

    public StarDeployNodeHandler(ISTARManager starManager) => _starManager = starManager;

    public ActionNodeType NodeType => ActionNodeType.StarDeploy;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<IdConfig>(context.Node.Config, nameof(ActionNodeType.StarDeploy), out var cfg, out var cfgError))
            return ActionNodeResults.Fail(cfgError);
        var r = await _starManager.DeployAsync(cfg.Id, context.ActingUserId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
