using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.StarGenerate"/> — relocated verbatim from ActionManager.</summary>
public sealed class StarGenerateNodeHandler : IActionNodeHandler
{
    private readonly ISTARManager _starManager;

    public StarGenerateNodeHandler(ISTARManager starManager) => _starManager = starManager;

    public ActionNodeType NodeType => ActionNodeType.StarGenerate;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<StarGenerateNodeConfig>(context.Node.Config, nameof(ActionNodeType.StarGenerate), out var cfg, out var cfgError))
            return ActionNodeResults.Fail(cfgError);
        var r = await _starManager.GenerateAsync(cfg.StarId, cfg.Request, context.ActingUserId);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
