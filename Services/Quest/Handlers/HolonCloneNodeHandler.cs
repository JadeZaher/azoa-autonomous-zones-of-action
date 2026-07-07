using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>Handles <see cref="QuestNodeType.HolonClone"/> — relocated verbatim from QuestManager.</summary>
public sealed class HolonCloneNodeHandler : IQuestNodeHandler
{
    private readonly IHolonManager _holonManager;

    public HolonCloneNodeHandler(IHolonManager holonManager) => _holonManager = holonManager;

    public QuestNodeType NodeType => QuestNodeType.HolonClone;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!QuestNodeConfig.TryDeserialize<HolonCloneNodeConfig>(context.Node.Config, nameof(QuestNodeType.HolonClone), out var cfg, out var cfgError))
            return QuestNodeResults.Fail(cfgError);
        var r = await _holonManager.CloneAsync(cfg.HolonId, cfg.Request, context.ActingAvatarId);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
