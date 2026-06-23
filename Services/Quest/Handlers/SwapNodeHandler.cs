using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>
/// Handles <see cref="QuestNodeType.Swap"/> — Tier-2 chain action. Mechanism
/// only: the tenant-supplied <c>SwapExecuteRequest</c> is passed straight through
/// to <see cref="ISwapManager.GetSwapTransactionAsync"/>; the DEX computes the
/// rate, AZOA never does. Requires a chain capability (a wallet bound to the
/// run); the engine refuses this node pre-execution when none is bound.
/// </summary>
public sealed class SwapNodeHandler : IQuestNodeHandler
{
    private readonly ISwapManager _swapManager;

    public SwapNodeHandler(ISwapManager swapManager) => _swapManager = swapManager;

    public QuestNodeType NodeType => QuestNodeType.Swap;

    public bool RequiresChainCapability => true;

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        var cfg = JsonSerializer.Deserialize<SwapNodeConfig>(context.Node.Config, QuestNodeJson.Options)!;

        // Idempotency key: QuestNodeExecutionContext exposes no dedicated
        // idempotency surface, so derive a stable key from the run+node identity.
        // It is unique per (run, node) re-evaluation and lets a future
        // server-broadcast swap path dedupe on it. The swap path returns an
        // UNSIGNED transaction today (client signs/broadcasts), so this is
        // forward-compatibility/audit only — see ISwapManager.GetSwapTransactionAsync.
        var idempotencyKey = $"{context.RunId}:{context.NodeId}";

        var r = await _swapManager.GetSwapTransactionAsync(cfg.Request, idempotencyKey);
        var outputJson = JsonSerializer.Serialize(r, QuestNodeJson.Options);
        if (r.IsError) return QuestNodeResults.Fail(r.Message);
        return QuestNodeResults.Ok(outputJson);
    }
}
