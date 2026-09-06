using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>
/// Handles <see cref="ActionNodeType.Swap"/> — Tier-2 chain action. Mechanism
/// only: the tenant-supplied <c>SwapExecuteRequest</c> is passed straight through
/// to <see cref="ISwapManager.GetSwapTransactionAsync"/>; the DEX computes the
/// rate, AZOA never does. Requires a chain capability (a wallet bound to the
/// run); the engine refuses this node pre-execution when none is bound.
/// </summary>
public sealed class SwapNodeHandler : IActionNodeHandler
{
    private readonly ISwapManager _swapManager;

    public SwapNodeHandler(ISwapManager swapManager) => _swapManager = swapManager;

    public ActionNodeType NodeType => ActionNodeType.Swap;

    public bool RequiresChainCapability => true;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<SwapNodeConfig>(context.Node.Config, nameof(ActionNodeType.Swap), out var cfg, out var cfgError))
            return ActionNodeResults.Fail(cfgError);

        // Idempotency key: ActionNodeExecutionContext exposes no dedicated
        // idempotency surface, so derive a stable key from the run+node identity.
        // It is unique per (run, node) re-evaluation and lets a future
        // server-broadcast swap path dedupe on it. The swap path returns an
        // UNSIGNED transaction today (client signs/broadcasts), so this is
        // forward-compatibility/audit only — see ISwapManager.GetSwapTransactionAsync.
        var idempotencyKey = $"{context.RunId}:{context.NodeId}";

        var r = await _swapManager.GetSwapTransactionAsync(cfg.Request, idempotencyKey);
        var outputJson = JsonSerializer.Serialize(r, ActionNodeJson.Options);
        if (r.IsError) return ActionNodeResults.Fail(r.Message);
        return ActionNodeResults.Ok(outputJson);
    }
}
