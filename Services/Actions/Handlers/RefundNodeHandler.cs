using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>
/// Handles <see cref="ActionNodeType.Refund"/> — Tier-2 chain action (D7).
/// Mechanically a reverse <see cref="INftManager.TransferAsync"/>, but the reversal
/// is DERIVED from a real upstream <see cref="ActionNodeType.Transfer"/> that debited
/// exactly this <c>NftId</c> earlier in the SAME run — never trusted from
/// caller-authored <see cref="RefundNodeConfig.Request"/> direction. Kept distinct
/// from <see cref="ActionNodeType.Transfer"/> by node type so the Track-2 saga can
/// declare "compensation = the Refund node". Actor is taken from the run context.
/// <para>
/// Drain-vector guard (HIGH): a Refund may ONLY reverse a debit that actually
/// happened upstream. If no succeeded upstream Transfer moved this NftId, the node
/// fails closed — a Refund can no longer be used as an out-of-order, no-prior-debit
/// drain. The reversal recipient is forced to the debit's original sender (the run
/// actor who ran the Transfer), so cfg.Request.TargetUserId cannot redirect it.
/// See Services/ActionDefinition/AGENTS.md §refund-linkage.
/// </para>
/// <para>
/// Soulbound assets fail closed: a true clawback of a non-transferable asset
/// needs a clawback primitive (deferred to H2 / signing D7), so the reversal is
/// refused with a clear message rather than silently no-op'd.
/// </para>
/// Requires a chain capability.
/// </summary>
public sealed class RefundNodeHandler : IActionNodeHandler
{
    private const string ClawbackDeferredMessage =
        "soulbound reversal requires clawback primitive — deferred (H2 / signing D7)";

    private readonly INftManager _nftManager;

    public RefundNodeHandler(INftManager nftManager) => _nftManager = nftManager;

    public ActionNodeType NodeType => ActionNodeType.Refund;

    public bool RequiresChainCapability => true;

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<RefundNodeConfig>(context.Node.Config, nameof(ActionNodeType.Refund), out var cfg, out var cfgError))
            return ActionNodeResults.Fail(cfgError);

        // Soulbound detection reads the asset to inspect metadata (see AGENTS.md
        // §refund-linkage). This is an UNSCOPED trusted read (callerUserId: null):
        // by the time a Refund runs, the upstream Transfer has reassigned the NFT's
        // holon.UserId to the recipient, so the runner no longer owns it — a
        // runner-scoped read would return "not found" and abort every legitimate
        // refund. The drain-vector guard below (not this read) is what authorizes it.
        var nft = await _nftManager.GetAsync(cfg.NftId, callerUserId: null);
        if (nft.IsError) return ActionNodeResults.Fail(nft.Message);
        if (nft.Result is { } asset && IsSoulbound(asset.Metadata))
            return ActionNodeResults.Fail(ClawbackDeferredMessage);

        // Drain-vector guard (HIGH): a Refund may only reverse a debit that ACTUALLY
        // happened upstream in THIS run. Require a succeeded upstream Transfer that
        // moved exactly cfg.NftId; refuse (fail closed) otherwise. This removes the
        // out-of-order / no-prior-debit drain where a Refund is just a second
        // attacker-directed Transfer.
        if (!TryFindReversibleTransfer(context, cfg.NftId, out var debit, out var debitError))
            return ActionNodeResults.Fail(debitError);

        // Derive the reversal from the real debit — NOT from cfg.Request's direction.
        // The upstream Transfer ran under this run's actor (recipient = original
        // sender), so the reversal returns the asset to that actor; the wallet is
        // reused from the debit. cfg.Request supplies only the (optional) memo.
        var reversal = new NftTransferRequest
        {
            TargetUserId = context.ActingUserId,
            WalletId = debit.Request.WalletId,
            Memo = cfg.Request.Memo,
        };

        // Actor is ALWAYS the run-context user; the config body user is ignored.
        // tenant-consent-delegation AC4: forward the run's acting tenant so a
        // tenant-driven refund (reverse transfer) stamps it on the op for the
        // seam's consent gate.
        var r = await _nftManager.TransferAsync(cfg.NftId, reversal, context.ActingUserId, actingTenantId: context.ActingTenantId);

        // Forward the broadcast tx hash on BOTH outcomes (a refund is a reverse
        // transfer): a broadcast-then-confirmation-timeout surfaces as IsError while
        // the reversal already landed, so the reconcile-before-retry engine must
        // verify chain truth instead of blind-retrying
        // (blockchain-recovery-and-portable-wallets §1.3).
        var opTxHash = ChainOperationOutputs.ReadTxHash(r.Result);
        var opChainType = ChainOperationOutputs.ReadChainType(r.Result);
        var outputJson = ActionNodeOutputProjection.SerializeOperation(r);

        if (r.IsError) return ActionNodeResults.Fail(r.Message, txHash: opTxHash, chainType: opChainType ?? "Algorand");
        return ActionNodeResults.Ok(outputJson, txHash: opTxHash, chainType: opChainType ?? "Algorand");
    }

    private const string NoUpstreamDebitMessage =
        "refund refused — no succeeded upstream Transfer in this run debited this asset; " +
        "a Refund may only reverse a real prior debit (drain-vector guard)";

    /// <summary>Finds a succeeded Transfer that debited <paramref name="nftId"/> anywhere earlier in this run.</summary>
    private static bool TryFindReversibleTransfer(
        ActionNodeExecutionContext context, Guid nftId, out TransferNodeConfig debit, out string error)
    {
        debit = default!;
        error = NoUpstreamDebitMessage;

        // Scan ALL run executions, not just direct-edge predecessors: the debit may
        // sit any number of hops upstream (e.g. Transfer → GateCheck → Refund), so
        // UpstreamExecutions (direct edges only) would fail closed on a valid refund.
        foreach (var (sourceNodeId, exec) in context.AllRunExecutions)
        {
            if (exec.State != ActionNodeState.Succeeded) continue;

            var srcNode = context.ActionDefinition.Nodes.FirstOrDefault(n => n.Id == sourceNodeId);
            if (srcNode is null || srcNode.NodeType != ActionNodeType.Transfer) continue;

            if (!ActionNodeConfig.TryDeserialize<TransferNodeConfig>(
                    srcNode.Config, nameof(ActionNodeType.Transfer), out var cfg, out _))
                continue;
            if (cfg.NftId != nftId) continue;

            debit = cfg;
            error = string.Empty;
            return true;
        }
        return false;
    }

    private static bool IsSoulbound(Dictionary<string, string> metadata)
    {
        foreach (var key in new[] { "soulbound", "isSoulbound", "is_soulbound" })
            if (metadata.TryGetValue(key, out var v) &&
                bool.TryParse(v, out var b) && b)
                return true;
        return false;
    }
}
