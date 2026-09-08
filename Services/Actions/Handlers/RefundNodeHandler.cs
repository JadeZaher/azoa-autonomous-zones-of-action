using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Hold;

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
/// See Services/Actions/AGENTS.md §refund-linkage.
/// </para>
/// <para>
/// Soulbound assets still fail closed here, but no longer for want of a primitive:
/// the clawback primitive exists (D19, <c>ActionNodeType.CredentialRevoke</c>). A
/// Refund is the WRONG authority for it — a refund reverses a debit for the run
/// actor, while a clawback is exercised by the credential's ISSUER against a holder
/// who did not consent. Letting Refund reach the clawback path would hand every
/// runner an issuer's power over an asset they merely received. Route soulbound
/// reversals through a CredentialRevoke node instead.
/// </para>
/// Requires a chain capability.
/// </summary>
public sealed class RefundNodeHandler : IActionNodeHandler
{
    /// <summary>
    /// Refusal message for a soulbound reversal. The behaviour is unchanged (fail
    /// closed); only the reason is now accurate — the clawback primitive shipped with
    /// D19, and a Refund is simply not authorised to use it.
    /// </summary>
    private const string ClawbackDeferredMessage =
        "soulbound reversal is not a Refund — the clawback primitive shipped with D19 and is " +
        "exercised by the credential's ISSUER (use a CredentialRevoke node), not by the run actor";

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

/// <summary>
/// Handles <see cref="ActionNodeType.HoldRefund"/> — Tier-2 chain action (D14 / W3.4).
/// Abandons a hold: the commitment goes back to the reserve partition it was drawn from
/// and NOTHING moves on chain, because a reserve-sourced hold's units never left the
/// holding account.
/// <para>
/// It is a separate node type from <see cref="ActionNodeType.Refund"/> above, and the
/// name is not an accident. That one reverses an NFT transfer under the run actor's own
/// authority over an asset the actor sent; this one un-commits custodial units under the
/// platform's authority over a ledger row. Overloading one node type across both would
/// have let an NFT reversal reach the hold ledger and vice versa, and the handler
/// registry enforces one handler per node type precisely so that cannot be papered over.
/// </para>
/// <para>
/// A hold whose release draw has already committed is REFUSED here rather than
/// un-committed: those units are counted as issued, so crediting the partition again
/// would hand the same budget out twice. That refusal lives in the hold service, at the
/// state machine, where both dispatch seams and the REST surface all reach it.
/// </para>
/// Requires a chain capability.
/// </summary>
public sealed class HoldRefundNodeHandler : IActionNodeHandler
{
    private readonly IPlatformHold _holds;

    public HoldRefundNodeHandler(IPlatformHold holds) => _holds = holds;

    public ActionNodeType NodeType => ActionNodeType.HoldRefund;

    public bool RequiresChainCapability => true;

    /// <inheritdoc />
    public string? ResolveSettlementRailId(ActionNode node) =>
        HoldNodeRail.Resolve<HoldRefundNodeConfig>(
            node, nameof(ActionNodeType.HoldRefund), cfg => cfg.ChainType);

    public async Task<ActionNodeHandlerResult> HandleAsync(
        ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<HoldRefundNodeConfig>(
                context.Node.Config, nameof(ActionNodeType.HoldRefund), out var cfg, out var cfgError))
        {
            return ActionNodeResults.Invalid(cfgError);
        }

        if (string.IsNullOrWhiteSpace(cfg.ChainType))
            return ActionNodeResults.Invalid("[HoldRefund] config must name the rail the hold settles on.");

        if (!HoldNodeTargetResolver.TryResolve(
                cfg.HoldId, cfg.HoldNodeName, context, nameof(ActionNodeType.HoldRefund),
                out var holdId, out var resolveError))
        {
            return ActionNodeResults.Invalid(resolveError);
        }

        var refunded = await _holds.RefundAsync(
            new HoldSettlementRequest(
                new HoldReference(cfg.ChainType!, holdId),
                ActingTenantId: context.ActingTenantId,
                Reason: cfg.Reason),
            ct);

        if (refunded.IsError || refunded.Result is null)
            return ActionNodeResults.Fail(refunded.Message, chainType: cfg.ChainType);

        return ActionNodeResults.Ok(
            HoldNodeResult.Serialize(refunded.Result),
            chainType: cfg.ChainType);
    }
}

/// <summary>Config for <see cref="ActionNodeType.HoldRefund"/>.</summary>
public sealed class HoldRefundNodeConfig
{
    /// <summary>Rail the hold settles on (e.g. <c>Algorand</c>).</summary>
    public string? ChainType { get; set; }

    /// <summary>The hold to give back. Mutually exclusive with <see cref="HoldNodeName"/>.</summary>
    public string? HoldId { get; set; }

    /// <summary>Name of the node in this action that opened the hold, when no id is given.</summary>
    public string? HoldNodeName { get; set; }

    /// <summary>Why the hold is being given back; recorded on the ledger row.</summary>
    public string? Reason { get; set; }
}

