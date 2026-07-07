using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>
/// Handles <see cref="QuestNodeType.Back"/> — Tier-2 chain action (final-hardening
/// D1 fractionalization rail). The reverse of a prior <see cref="QuestNodeType.Bridge"/>:
/// burns the wrapped asset on the target chain and releases the original on the
/// source chain via the REAL Phase-B
/// <see cref="ICrossChainBridgeService.ReverseBridgeAsync"/>. The reverse burn is an
/// on-chain effect that the bridge idempotency-gates (no double-burn); a Solana leg
/// is fail-closed at the provider level and surfaces here as a node failure. The
/// node MOVES value only; peg/valuation stays tenant-side (Emit). See
/// Services/Quest/AGENTS.md §fractionalization.
/// </summary>
public sealed class BackNodeHandler : IQuestNodeHandler
{
    private readonly ICrossChainBridgeService _bridge;

    public BackNodeHandler(ICrossChainBridgeService bridge) => _bridge = bridge;

    public QuestNodeType NodeType => QuestNodeType.Back;

    public bool RequiresChainCapability => true;

    public async Task<QuestNodeHandlerResult> HandleAsync(
        QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!QuestNodeConfig.TryDeserialize<BackNodeConfig>(context.Node.Config, nameof(QuestNodeType.Back), out var cfg, out var cfgError))
            return QuestNodeResults.Fail(cfgError);

        if (string.IsNullOrWhiteSpace(cfg.BridgeTransactionId))
            return QuestNodeResults.Fail("[Back] BridgeTransactionId is required (bind it from the upstream Bridge node output).");
        if (string.IsNullOrWhiteSpace(cfg.SourceRecipientAddress))
            return QuestNodeResults.Fail("[Back] SourceRecipientAddress is required.");

        // Idempotency seed: stable per (run, node) so a re-evaluated Back node
        // dedupes to ONE reverse burn (the bridge service avatar-namespaces it).
        var clientIdempotencyKey = $"{context.RunId}:{context.NodeId}";

        // Actor is ALWAYS the run-context avatar. Passing it as callerAvatarId
        // IDOR-scopes the reverse to that avatar's own bridge rows — a run can only
        // reverse a bridge it (its avatar) owns; a mismatch surfaces as "not found".
        var r = await _bridge.ReverseBridgeAsync(
            cfg.BridgeTransactionId, cfg.SourceRecipientAddress, ct,
            clientIdempotencyKey, context.ActingAvatarId);

        var outputJson = JsonSerializer.Serialize(r.Result, QuestNodeJson.Options);
        if (r.IsError || r.Result is null) return QuestNodeResults.Fail(r.Message);

        // RedemptionTxHash carries the reverse burn tx; forward it + the source
        // chain for reconcile-before-retry.
        return QuestNodeResults.Ok(
            outputJson,
            txHash: r.Result.RedemptionTxHash,
            chainType: r.Result.SourceChain);
    }
}
