using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>
/// Handles <see cref="ActionNodeType.Back"/> — Tier-2 chain action (final-hardening
/// D1 fractionalization rail). The reverse of a prior <see cref="ActionNodeType.Bridge"/>:
/// burns the wrapped asset on the target chain and releases the original on the
/// source chain via the REAL Phase-B
/// <see cref="ICrossChainBridgeService.ReverseBridgeAsync"/>. The reverse burn is an
/// on-chain effect that the bridge idempotency-gates (no double-burn); a Solana leg
/// is fail-closed at the provider level and surfaces here as a node failure. The
/// node MOVES value only; peg/valuation stays tenant-side (Emit). See
/// Services/Actions/AGENTS.md §fractionalization.
/// </summary>
public sealed class BackNodeHandler : IActionNodeHandler
{
    private readonly ICrossChainBridgeService _bridge;

    public BackNodeHandler(ICrossChainBridgeService bridge) => _bridge = bridge;

    public ActionNodeType NodeType => ActionNodeType.Back;

    public bool RequiresChainCapability => true;

    public async Task<ActionNodeHandlerResult> HandleAsync(
        ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<BackNodeConfig>(context.Node.Config, nameof(ActionNodeType.Back), out var cfg, out var cfgError))
            return ActionNodeResults.Fail(cfgError);

        if (string.IsNullOrWhiteSpace(cfg.BridgeTransactionId))
            return ActionNodeResults.Fail("[Back] BridgeTransactionId is required (bind it from the upstream Bridge node output).");
        if (string.IsNullOrWhiteSpace(cfg.SourceRecipientAddress))
            return ActionNodeResults.Fail("[Back] SourceRecipientAddress is required.");

        // Idempotency seed: stable per (run, node) so a re-evaluated Back node
        // dedupes to ONE reverse burn (the bridge service user-namespaces it).
        var clientIdempotencyKey = $"{context.RunId}:{context.NodeId}";

        // Actor is ALWAYS the run-context user. Passing it as callerUserId
        // IDOR-scopes the reverse to that user's own bridge rows — a run can only
        // reverse a bridge it (its user) owns; a mismatch surfaces as "not found".
        var r = await _bridge.ReverseBridgeAsync(
            cfg.BridgeTransactionId, cfg.SourceRecipientAddress, ct,
            clientIdempotencyKey, context.ActingUserId);

        var outputJson = JsonSerializer.Serialize(r.Result, ActionNodeJson.Options);
        if (r.IsError || r.Result is null) return ActionNodeResults.Fail(r.Message);

        // RedemptionTxHash carries the reverse burn tx; forward it + the source
        // chain for reconcile-before-retry.
        return ActionNodeResults.Ok(
            outputJson,
            txHash: r.Result.RedemptionTxHash,
            chainType: r.Result.SourceChain);
    }
}
