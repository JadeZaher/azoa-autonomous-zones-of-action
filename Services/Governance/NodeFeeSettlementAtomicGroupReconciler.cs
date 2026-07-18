using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;

namespace AZOA.WebAPI.Services.Governance;

/// <inheritdoc/>
public sealed class NodeFeeSettlementAtomicGroupReconciler : INodeFeeSettlementAtomicGroupReconciler
{
    private const string MissingReceiptReason =
        "Accepted atomic group receipt is absent or does not satisfy durable reconstruction invariants.";
    private const string ObservationUnavailableReason =
        "Atomic group observation is unavailable; settlement remains nonterminal.";

    private readonly INodeFeeSettlementStore _store;
    private readonly IBlockchainProviderFactory _providers;

    public NodeFeeSettlementAtomicGroupReconciler(
        INodeFeeSettlementStore store,
        IBlockchainProviderFactory providers)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeFeeSettlementAtomicGroupReconciliationReport>> ReconcileDueAsync(
        NodeFeeSettlementRecoveryRequest request,
        CancellationToken ct = default)
    {
        var requestError = NodeFeeSettlementRecoveryRequestValidator.Validate(request);
        if (requestError is not null)
            return AZOAResult<NodeFeeSettlementAtomicGroupReconciliationReport>.Failure(requestError);

        var candidates = await _store.ListRecoverableAsync(request.Now, request.BatchSize, ct);
        if (candidates.IsError || candidates.Result is null)
        {
            return AZOAResult<NodeFeeSettlementAtomicGroupReconciliationReport>.Failure(
                $"Node fee settlement reconciliation unavailable: {candidates.Message}");
        }

        var claimed = 0;
        var settled = 0;
        var nonTerminal = 0;
        var deferred = 0;
        var contended = 0;
        var leaseExpiresAt = request.Now.Add(request.LeaseDuration);
        var nextAttemptAt = request.Now.Add(request.RetryDelay);

        foreach (var candidate in candidates.Result)
        {
            ct.ThrowIfCancellationRequested();
            var claim = await _store.TryClaimAcceptedAtomicGroupRecoveryAsync(
                candidate,
                Guid.NewGuid().ToString("N"),
                request.Now,
                leaseExpiresAt,
                ct);
            if (claim.IsError)
            {
                return AZOAResult<NodeFeeSettlementAtomicGroupReconciliationReport>.Failure(
                    $"Node fee settlement reconciliation unavailable: {claim.Message}");
            }

            if (claim.Result is null)
            {
                contended++;
                continue;
            }

            claimed++;
            var lease = NodeFeeSettlementRecoveryLease.FromClaim(claim.Result);
            var receipt = await _store.GetAcceptedAtomicGroupAsync(claim.Result.Id, ct);
            if (receipt.IsError)
                return Failure(receipt.Message);

            if (receipt.Result is null)
            {
                var result = await DeferAsync(lease, MissingReceiptReason, nextAttemptAt, request.Now, ct);
                if (result.IsError)
                    return Failure(result.Message);
                if (result.Result)
                    deferred++;
                else
                    contended++;
                continue;
            }

            var evidence = TryBuildEvidence(claim.Result, receipt.Result);
            if (evidence.IsError || evidence.Result is null)
            {
                var result = await DeferAsync(lease, MissingReceiptReason, nextAttemptAt, request.Now, ct);
                if (result.IsError)
                    return Failure(result.Message);
                if (result.Result)
                    deferred++;
                else
                    contended++;
                continue;
            }

            if (!TryResolveObserver(evidence.Result, out var observer) || observer is null)
            {
                var result = await RecordNonTerminalAsync(
                    lease,
                    UnknownEffects(),
                    ObservationUnavailableReason,
                    nextAttemptAt,
                    request.Now,
                    ct);
                if (result.IsError)
                    return Failure(result.Message);
                if (result.Result)
                    nonTerminal++;
                else
                    contended++;
                continue;
            }

            var observation = await observer.ObserveAtomicTransferGroupAsync(evidence.Result, ct);
            if (observation.IsError || observation.Result is null)
            {
                var result = await RecordNonTerminalAsync(
                    lease,
                    UnknownEffects(),
                    ObservationUnavailableReason,
                    nextAttemptAt,
                    request.Now,
                    ct);
                if (result.IsError)
                    return Failure(result.Message);
                if (result.Result)
                    nonTerminal++;
                else
                    contended++;
                continue;
            }

            if (IsExactConfirmed(observation.Result, receipt.Result))
            {
                var result = await _store.TrySettlePairedAsync(
                    lease,
                    NodeFeeSettlementTerminalization.FromParentIdempotencyKeyHash(
                        claim.Result.ParentIdempotencyKeyHash,
                        receipt.Result.PrimaryTransactionId,
                        receipt.Result.TreasuryTransactionId,
                        TerminalPayload(claim.Result, receipt.Result, observation.Result.Primary.ConfirmedRound!.Value)),
                    request.Now,
                    ct);
                if (result.IsError)
                    return Failure(result.Message);
                if (result.Result)
                    settled++;
                else
                    contended++;
                continue;
            }

            var nonTerminalResult = await RecordNonTerminalAsync(
                lease,
                ToEffects(observation.Result, receipt.Result),
                $"Atomic group observation is {observation.Result.Verdict}.",
                nextAttemptAt,
                request.Now,
                ct);
            if (nonTerminalResult.IsError)
                return Failure(nonTerminalResult.Message);
            if (nonTerminalResult.Result)
                nonTerminal++;
            else
                contended++;
        }

        return AZOAResult<NodeFeeSettlementAtomicGroupReconciliationReport>.Success(
            new NodeFeeSettlementAtomicGroupReconciliationReport(
                candidates.Result.Count, claimed, settled, nonTerminal, deferred, contended),
            "Atomic-group receipt reconciliation completed without chain submission.");
    }

    private bool TryResolveObserver(
        AtomicTransferGroupObservationEvidence evidence,
        out IAtomicTransferGroupObservationModule? observer)
    {
        observer = null;
        if (!Enum.IsDefined(evidence.Network))
            return false;

        try
        {
            var provider = _providers.GetProvider(evidence.ChainType, evidence.Network);
            if (!string.Equals(provider.ChainType, evidence.ChainType, StringComparison.OrdinalIgnoreCase)
                || provider.ActiveNetwork != evidence.Network)
            {
                return false;
            }

            return provider.TryGetModule(out observer) && observer is not null;
        }
        catch (BlockchainProviderNotFoundException)
        {
            return false;
        }
    }

    private static AZOAResult<AtomicTransferGroupObservationEvidence> TryBuildEvidence(
        NodeFeeSettlement settlement,
        NodeFeeAtomicGroup receipt)
    {
        if (!IsReceiptBoundToSettlement(settlement, receipt)
            || !string.Equals(settlement.ExpectedAtomicGroupIdentity, receipt.GroupIdentity, StringComparison.Ordinal)
            || !string.Equals(settlement.PrimaryTransactionHash, receipt.PrimaryTransactionId, StringComparison.Ordinal)
            || !string.Equals(settlement.FeeTransactionHash, receipt.TreasuryTransactionId, StringComparison.Ordinal)
            || !NodeFeeSettlement.TryParseCanonicalPositiveBaseUnitAmount(settlement.GrossAmount, out var gross)
            || !NodeFeeSettlement.TryParseCanonicalPositiveBaseUnitAmount(settlement.FeeAmount, out var fee)
            || !NodeFeeSettlement.TryParseCanonicalPositiveBaseUnitAmount(settlement.NetAmount, out var net)
            || (UInt128)fee + net != gross
            || !TryMapAcceptedState(receipt.State, out var acceptedState))
        {
            return AZOAResult<AtomicTransferGroupObservationEvidence>.Failure(MissingReceiptReason);
        }

        return AtomicTransferGroupObservationEvidence.TryCreate(
            settlement.Chain,
            Enum.TryParse<ChainNetwork>(settlement.Network, true, out var network) ? network : default,
            settlement.ExpectedAtomicGroupIdentity,
            new AtomicTransferObservationEffect(
                settlement.AssetId,
                receipt.SourceAddress,
                receipt.PrimaryRecipientAddress,
                net),
            new AtomicTransferObservationEffect(
                settlement.AssetId,
                receipt.SourceAddress,
                settlement.TreasuryAddress,
                fee),
            receipt.ChainGroupId,
            receipt.PrimaryTransactionId,
            receipt.TreasuryTransactionId,
            acceptedState,
            receipt.GroupIdentity);
    }

    private static bool IsReceiptBoundToSettlement(NodeFeeSettlement settlement, NodeFeeAtomicGroup receipt)
        => NodeFeeAtomicGroup.IsBoundToSettlement(receipt, settlement.Id)
           && string.Equals(receipt.Id, NodeFeeAtomicGroup.RecordIdFor(settlement.Id), StringComparison.Ordinal);

    private static bool TryMapAcceptedState(
        NodeFeeAtomicGroup.StateKind state,
        out AtomicTransferGroupSubmissionState acceptedState)
    {
        acceptedState = state switch
        {
            NodeFeeAtomicGroup.StateKind.Submitted => AtomicTransferGroupSubmissionState.Submitted,
            NodeFeeAtomicGroup.StateKind.PendingConfirmation => AtomicTransferGroupSubmissionState.PendingConfirmation,
            NodeFeeAtomicGroup.StateKind.Confirmed => AtomicTransferGroupSubmissionState.Confirmed,
            _ => AtomicTransferGroupSubmissionState.NotSubmitted,
        };
        return acceptedState != AtomicTransferGroupSubmissionState.NotSubmitted;
    }

    private static bool IsExactConfirmed(
        AtomicTransferGroupObservation observation,
        NodeFeeAtomicGroup receipt)
        => observation.Verdict == AtomicTransferGroupObservationVerdict.Confirmed
           && observation.Primary.Verdict == AtomicTransferLegObservationVerdict.Confirmed
           && observation.Treasury.Verdict == AtomicTransferLegObservationVerdict.Confirmed
           && string.Equals(observation.Primary.TransactionId, receipt.PrimaryTransactionId, StringComparison.Ordinal)
           && string.Equals(observation.Treasury.TransactionId, receipt.TreasuryTransactionId, StringComparison.Ordinal)
           && observation.Primary.ConfirmedRound is > 0
           && observation.Primary.ConfirmedRound == observation.Treasury.ConfirmedRound;

    private static NodeFeeSettlementEffectReconciliation ToEffects(
        AtomicTransferGroupObservation observation,
        NodeFeeAtomicGroup receipt)
    {
        var primary = ToEffect(observation.Primary, receipt.PrimaryTransactionId);
        var treasury = ToEffect(observation.Treasury, receipt.TreasuryTransactionId);
        var effects = new NodeFeeSettlementEffectReconciliation(
            primary.State,
            primary.Reference,
            treasury.State,
            treasury.Reference);
        return effects.IsNonTerminal ? effects : UnknownEffects();
    }

    private static NodeFeeSettlementEffectReconciliation UnknownEffects() => new(
        NodeFeeSettlement.EffectStateKind.Unknown,
        null,
        NodeFeeSettlement.EffectStateKind.Unknown,
        null);

    private static (NodeFeeSettlement.EffectStateKind State, string? Reference) ToEffect(
        AtomicTransferLegObservation observation,
        string expectedTransactionId)
    {
        if (observation.Verdict == AtomicTransferLegObservationVerdict.Confirmed
            && string.Equals(observation.TransactionId, expectedTransactionId, StringComparison.Ordinal))
        {
            return (NodeFeeSettlement.EffectStateKind.Confirmed, expectedTransactionId);
        }

        return observation.Verdict is AtomicTransferLegObservationVerdict.PoolRejected
                or AtomicTransferLegObservationVerdict.Mismatched
                || observation.Verdict == AtomicTransferLegObservationVerdict.Confirmed
            ? (NodeFeeSettlement.EffectStateKind.Failed, null)
            : (NodeFeeSettlement.EffectStateKind.Unknown, null);
    }

    private Task<AZOAResult<bool>> DeferAsync(
        NodeFeeSettlementRecoveryLease lease,
        string reason,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset now,
        CancellationToken ct)
        => _store.TryDeferToReconciliationAsync(lease, reason, nextAttemptAt, now, ct);

    private async Task<AZOAResult<bool>> RecordNonTerminalAsync(
        NodeFeeSettlementRecoveryLease lease,
        NodeFeeSettlementEffectReconciliation reconciliation,
        string reason,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset now,
        CancellationToken ct)
        => await _store.TryRecordNonTerminalReconciliationAsync(
            lease, reconciliation, reason, nextAttemptAt, now, ct);

    private static string TerminalPayload(
        NodeFeeSettlement settlement,
        NodeFeeAtomicGroup receipt,
        long confirmedRound)
        => JsonSerializer.Serialize(new
        {
            settlementId = settlement.Id,
            groupIdentity = receipt.GroupIdentity,
            chainGroupId = receipt.ChainGroupId,
            primaryTransactionId = receipt.PrimaryTransactionId,
            treasuryTransactionId = receipt.TreasuryTransactionId,
            confirmedRound,
        });

    private static AZOAResult<NodeFeeSettlementAtomicGroupReconciliationReport> Failure(string message)
        => AZOAResult<NodeFeeSettlementAtomicGroupReconciliationReport>.Failure(
            $"Node fee settlement reconciliation unavailable: {message}");
}
