using System.Text.Json;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Interfaces.QuestExecution;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Services.Quest.Handlers;

/// <summary>Handles <see cref="QuestNodeType.StarGenerate"/> — relocated verbatim from QuestManager.</summary>
public sealed class StarGenerateNodeHandler : IQuestNodeHandler
{
    private readonly ISTARManager _starManager;

    public StarGenerateNodeHandler(ISTARManager starManager) => _starManager = starManager;

    public QuestNodeType NodeType => QuestNodeType.StarGenerate;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        var cfg = JsonSerializer.Deserialize<StarGenerateNodeConfig>(context.Node.Config, QuestNodeJson.Options)!;
        var r = await _starManager.GenerateAsync(cfg.StarId, cfg.Request);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
