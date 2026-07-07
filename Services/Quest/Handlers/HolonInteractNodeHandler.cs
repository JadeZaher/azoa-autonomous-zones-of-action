using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>Handles <see cref="QuestNodeType.HolonInteract"/> — relocated verbatim from QuestManager.</summary>
public sealed class HolonInteractNodeHandler : IQuestNodeHandler
{
    private readonly IHolonManager _holonManager;

    public HolonInteractNodeHandler(IHolonManager holonManager) => _holonManager = holonManager;

    public QuestNodeType NodeType => QuestNodeType.HolonInteract;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!QuestNodeConfig.TryDeserialize<HolonInteractNodeConfig>(context.Node.Config, nameof(QuestNodeType.HolonInteract), out var cfg, out var cfgError))
            return QuestNodeResults.Fail(cfgError);
        // C-2: scope to the runner (ActingAvatarId) so a marketplace run cannot mutate a victim's holon by GUID.
        var r = await _holonManager.InteractAsync(cfg.HolonId, cfg.Request, context.ActingAvatarId);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
