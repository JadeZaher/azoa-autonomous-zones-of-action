using System.Text.Json;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Interfaces.QuestExecution;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Services.Quest.Handlers;

/// <summary>Handles <see cref="QuestNodeType.BlockchainExecute"/> — relocated verbatim from QuestManager.</summary>
public sealed class BlockchainExecuteNodeHandler : IQuestNodeHandler
{
    private readonly IBlockchainOperationManager _blockchainManager;

    public BlockchainExecuteNodeHandler(IBlockchainOperationManager blockchainManager) => _blockchainManager = blockchainManager;

    public QuestNodeType NodeType => QuestNodeType.BlockchainExecute;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        var cfg = JsonSerializer.Deserialize<IdConfig>(context.Node.Config, QuestNodeJson.Options)!;
        var r = await _blockchainManager.GetAsync(cfg.Id);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
