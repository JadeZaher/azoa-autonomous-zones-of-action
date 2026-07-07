using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>Handles <see cref="QuestNodeType.HolonCompose"/> — relocated verbatim from QuestManager.</summary>
public sealed class HolonComposeNodeHandler : IQuestNodeHandler
{
    private readonly IHolonManager _holonManager;

    public HolonComposeNodeHandler(IHolonManager holonManager) => _holonManager = holonManager;

    public QuestNodeType NodeType => QuestNodeType.HolonCompose;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!QuestNodeConfig.TryDeserialize<IdConfig>(context.Node.Config, nameof(QuestNodeType.HolonCompose), out var cfg, out var cfgError))
            return QuestNodeResults.Fail(cfgError);
        var r = await _holonManager.ComposeAsync(cfg.Id, context.ActingAvatarId);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
