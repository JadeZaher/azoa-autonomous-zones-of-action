using System.Text.Json;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Interfaces.QuestExecution;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Services.Quest.Handlers;

/// <summary>Handles <see cref="QuestNodeType.HolonMoveSubtree"/> — relocated verbatim from QuestManager.</summary>
public sealed class HolonMoveSubtreeNodeHandler : IQuestNodeHandler
{
    private readonly IHolonManager _holonManager;

    public HolonMoveSubtreeNodeHandler(IHolonManager holonManager) => _holonManager = holonManager;

    public QuestNodeType NodeType => QuestNodeType.HolonMoveSubtree;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        var cfg = JsonSerializer.Deserialize<HolonMoveNodeConfig>(context.Node.Config, QuestNodeJson.Options)!;
        var r = await _holonManager.MoveSubtreeAsync(cfg.HolonId, cfg.NewParentId);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
