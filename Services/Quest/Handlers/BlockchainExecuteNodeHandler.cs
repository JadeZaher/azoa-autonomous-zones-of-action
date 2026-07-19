using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>Handles <see cref="QuestNodeType.BlockchainExecute"/> — relocated verbatim from QuestManager.</summary>
public sealed class BlockchainExecuteNodeHandler : IQuestNodeHandler
{
    private readonly IBlockchainOperationManager _blockchainManager;

    public BlockchainExecuteNodeHandler(IBlockchainOperationManager blockchainManager) => _blockchainManager = blockchainManager;

    public QuestNodeType NodeType => QuestNodeType.BlockchainExecute;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!QuestNodeConfig.TryDeserialize<IdConfig>(context.Node.Config, nameof(QuestNodeType.BlockchainExecute), out var cfg, out var cfgError))
            return QuestNodeResults.Fail(cfgError);
        var r = await _blockchainManager.GetAsync(cfg.Id);
        var outputJson = QuestNodeOutputProjection.SerializeOperation(r);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
