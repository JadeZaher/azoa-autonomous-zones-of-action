using System.Text.Json;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>
/// Handles <see cref="ActionNodeType.Bridge"/> — Tier-2 chain action (final-hardening
/// D1 fractionalization rail). Locks/bridges an asset cross-chain through the REAL
/// Phase-B <see cref="ICrossChainBridgeService.InitiateBridgeAsync"/>: on an Algorand
/// route providers must advertise the complete lock/mint/burn/release lifecycle or
/// the operation fails closed. The node MOVES value only and derives no economic meaning; peg/valuation
/// stays tenant-side (Emit). See Services/Actions/AGENTS.md §fractionalization.
/// </summary>
public sealed class BridgeNodeHandler : IActionNodeHandler
{
    private readonly ICrossChainBridgeService _bridge;

    public BridgeNodeHandler(ICrossChainBridgeService bridge) => _bridge = bridge;

    public ActionNodeType NodeType => ActionNodeType.Bridge;

    public bool RequiresChainCapability => true;

    /// <summary>
    /// A bridge settles its outbound leg on the SOURCE chain — that is where the actor's
    /// value has to be, and where the lock is signed. The target chain is the recipient's
    /// problem and is addressed explicitly in the config.
    /// </summary>
    public string? ResolveSettlementRailId(ActionNode node) =>
        ActionNodeConfig.TryDeserialize<BridgeNodeConfig>(
            node.Config, nameof(ActionNodeType.Bridge), out var cfg, out _)
            ? cfg.SourceChain
            : null;

    public async Task<ActionNodeHandlerResult> HandleAsync(
        ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<BridgeNodeConfig>(context.Node.Config, nameof(ActionNodeType.Bridge), out var cfg, out var cfgError))
            return ActionNodeResults.Fail(cfgError);

        // Optional mode: an unrecognised value fails the node closed rather than
        // silently defaulting to a mode the tenant did not intend.
        BridgeMode? mode = null;
        if (!string.IsNullOrWhiteSpace(cfg.Mode))
        {
            if (!Enum.TryParse<BridgeMode>(cfg.Mode, ignoreCase: true, out var parsed))
                return ActionNodeResults.Fail($"[Bridge] unknown bridge mode '{cfg.Mode}' (expected Trusted or Wormhole)");
            mode = parsed;
        }

        // Idempotency seed: ActionNodeExecutionContext exposes no client-idempotency
        // surface, so derive a STABLE key from the (run, node) identity (Swap/Grant
        // precedent). It is passed as the client idempotency key; the bridge service
        // user-namespaces it, so a re-evaluation of the same node dedupes to ONE
        // irreversible lock/bridge — no double-bridge.
        var clientIdempotencyKey = $"{context.RunId}:{context.NodeId}";

        // Actor is ALWAYS the run-context user; the config body carries no user
        // (Grant/Transfer precedent). The bridge stamps the row to this user so the
        // Back node's reverse can be IDOR-scoped to the same owner.
        var r = await _bridge.InitiateBridgeAsync(
            cfg.SourceChain, cfg.TargetChain, cfg.TokenId,
            cfg.RecipientAddress, context.ActingUserId, cfg.Amount,
            mode, ct, clientIdempotencyKey);

        var outputJson = JsonSerializer.Serialize(r.Result, ActionNodeJson.Options);
        if (r.IsError || r.Result is null) return ActionNodeResults.Fail(r.Message);

        // Success output carries the bridge transaction id + lock tx hash so a
        // downstream Back node can $from-bind BridgeTransactionId, and the
        // reconcile-before-retry engine can verify chain truth on a later failure.
        return ActionNodeResults.Ok(
            outputJson,
            txHash: r.Result.LockTxHash,
            chainType: cfg.SourceChain);
    }
}
