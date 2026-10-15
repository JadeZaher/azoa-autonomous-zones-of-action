using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>Handles <see cref="ActionNodeType.BlockchainExecute"/> — relocated verbatim from ActionManager.</summary>
public sealed class BlockchainExecuteNodeHandler : IActionNodeHandler
{
    private readonly IBlockchainOperationManager _blockchainManager;

    public BlockchainExecuteNodeHandler(IBlockchainOperationManager blockchainManager) => _blockchainManager = blockchainManager;

    public ActionNodeType NodeType => ActionNodeType.BlockchainExecute;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<IdConfig>(context.Node.Config, nameof(ActionNodeType.BlockchainExecute), out var cfg, out var cfgError))
            return ActionNodeResults.Fail(cfgError);
        var r = await _blockchainManager.GetAsync(cfg.Id);
        var outputJson = ActionNodeOutputProjection.SerializeOperation(r);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
