using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>Handles <see cref="QuestNodeType.AvatarNFTGetComposite"/> — relocated verbatim from QuestManager.</summary>
public sealed class AvatarNFTGetCompositeNodeHandler : IQuestNodeHandler
{
    private readonly IAvatarNFTService _avatarNFTService;

    public AvatarNFTGetCompositeNodeHandler(IAvatarNFTService avatarNFTService) => _avatarNFTService = avatarNFTService;

    public QuestNodeType NodeType => QuestNodeType.AvatarNFTGetComposite;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!QuestNodeConfig.TryDeserialize<IdConfig>(context.Node.Config, nameof(QuestNodeType.AvatarNFTGetComposite), out var cfg, out var cfgError))
            return QuestNodeResults.Fail(cfgError);
        var r = await _avatarNFTService.GetAvatarNFTCompositeAsync(cfg.Id);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
