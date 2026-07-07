using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>Handles <see cref="QuestNodeType.HolonUpdate"/> — relocated verbatim from QuestManager.</summary>
public sealed class HolonUpdateNodeHandler : IQuestNodeHandler
{
    private readonly IHolonManager _holonManager;

    public HolonUpdateNodeHandler(IHolonManager holonManager) => _holonManager = holonManager;

    public QuestNodeType NodeType => QuestNodeType.HolonUpdate;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!QuestNodeConfig.TryDeserialize<HolonUpdateNodeConfig>(context.Node.Config, nameof(QuestNodeType.HolonUpdate), out var cfg, out var cfgError))
            return QuestNodeResults.Fail(cfgError);
        // C1: scope the update to the acting avatar (the RUNNER) so a marketplace
        // run can only mutate the runner's own holons — never the quest owner's.
        var r = await _holonManager.UpdateAsync(cfg.HolonId, cfg.Model, context.ActingAvatarId);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
