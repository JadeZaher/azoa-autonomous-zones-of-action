using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>Handles <see cref="QuestNodeType.StarDeploy"/> — relocated verbatim from QuestManager.</summary>
public sealed class StarDeployNodeHandler : IQuestNodeHandler
{
    private readonly ISTARManager _starManager;

    public StarDeployNodeHandler(ISTARManager starManager) => _starManager = starManager;

    public QuestNodeType NodeType => QuestNodeType.StarDeploy;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!QuestNodeConfig.TryDeserialize<IdConfig>(context.Node.Config, nameof(QuestNodeType.StarDeploy), out var cfg, out var cfgError))
            return QuestNodeResults.Fail(cfgError);
        var r = await _starManager.DeployAsync(cfg.Id, context.ActingAvatarId);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
