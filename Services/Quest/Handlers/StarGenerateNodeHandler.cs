using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>Handles <see cref="QuestNodeType.StarGenerate"/> — relocated verbatim from QuestManager.</summary>
public sealed class StarGenerateNodeHandler : IQuestNodeHandler
{
    private readonly ISTARManager _starManager;

    public StarGenerateNodeHandler(ISTARManager starManager) => _starManager = starManager;

    public QuestNodeType NodeType => QuestNodeType.StarGenerate;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!QuestNodeConfig.TryDeserialize<StarGenerateNodeConfig>(context.Node.Config, nameof(QuestNodeType.StarGenerate), out var cfg, out var cfgError))
            return QuestNodeResults.Fail(cfgError);
        var r = await _starManager.GenerateAsync(cfg.StarId, cfg.Request);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
