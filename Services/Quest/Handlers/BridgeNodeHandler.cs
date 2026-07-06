using System.Text.Json;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>
/// Handles <see cref="QuestNodeType.Bridge"/> — Tier-2 chain action (final-hardening
/// D1 fractionalization rail). Locks/bridges an asset cross-chain through the REAL
/// Phase-B <see cref="ICrossChainBridgeService.InitiateBridgeAsync"/>: on an Algorand
/// route the lock/burn is a real broadcast; a Solana route is fail-closed at the
/// provider level and surfaces here as a node failure (correct — never fabricated
/// success). The node MOVES value only and derives no economic meaning; peg/valuation
/// stays tenant-side (Emit). See Services/Quest/AGENTS.md §fractionalization.
/// </summary>
public sealed class BridgeNodeHandler : IQuestNodeHandler
{
    private readonly ICrossChainBridgeService _bridge;

    public BridgeNodeHandler(ICrossChainBridgeService bridge) => _bridge = bridge;

    public QuestNodeType NodeType => QuestNodeType.Bridge;

    public bool RequiresChainCapability => true;

    public async Task<QuestNodeHandlerResult> HandleAsync(
        QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!QuestNodeConfig.TryDeserialize<BridgeNodeConfig>(context.Node.Config, nameof(QuestNodeType.Bridge), out var cfg, out var cfgError))
            return QuestNodeResults.Fail(cfgError);

        // Optional mode: an unrecognised value fails the node closed rather than
        // silently defaulting to a mode the tenant did not intend.
        BridgeMode? mode = null;
        if (!string.IsNullOrWhiteSpace(cfg.Mode))
        {
            if (!Enum.TryParse<BridgeMode>(cfg.Mode, ignoreCase: true, out var parsed))
                return QuestNodeResults.Fail($"[Bridge] unknown bridge mode '{cfg.Mode}' (expected Trusted or Wormhole)");
            mode = parsed;
        }

        // Idempotency seed: QuestNodeExecutionContext exposes no client-idempotency
        // surface, so derive a STABLE key from the (run, node) identity (Swap/Grant
        // precedent). It is passed as the client idempotency key; the bridge service
        // avatar-namespaces it, so a re-evaluation of the same node dedupes to ONE
        // irreversible lock/bridge — no double-bridge.
        var clientIdempotencyKey = $"{context.RunId}:{context.NodeId}";

        // Actor is ALWAYS the run-context avatar; the config body carries no avatar
        // (Grant/Transfer precedent). The bridge stamps the row to this avatar so the
        // Back node's reverse can be IDOR-scoped to the same owner.
        var r = await _bridge.InitiateBridgeAsync(
            cfg.SourceChain, cfg.TargetChain, cfg.TokenId,
            cfg.RecipientAddress, context.Quest.AvatarId, cfg.Amount,
            mode, ct, clientIdempotencyKey);

        var outputJson = JsonSerializer.Serialize(r.Result, QuestNodeJson.Options);
        if (r.IsError || r.Result is null) return QuestNodeResults.Fail(r.Message);

        // Success output carries the bridge transaction id + lock tx hash so a
        // downstream Back node can $from-bind BridgeTransactionId, and the
        // reconcile-before-retry engine can verify chain truth on a later failure.
        return QuestNodeResults.Ok(
            outputJson,
            txHash: r.Result.LockTxHash,
            chainType: cfg.SourceChain);
    }
}
