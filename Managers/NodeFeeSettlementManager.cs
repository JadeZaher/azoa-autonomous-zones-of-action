using System.Globalization;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Managers;

/// <inheritdoc/>
public sealed class NodeFeeSettlementManager : INodeFeeSettlementManager
{
    private readonly INodeFeeSettlementStore _store;

    public NodeFeeSettlementManager(INodeFeeSettlementStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeFeeSettlement>> PrepareAsync(
        NodeFeeSettlementDraft draft,
        CancellationToken ct = default)
    {
        var validation = Validate(draft);
        if (validation.IsError || validation.Result is null)
            return AZOAResult<NodeFeeSettlement>.Failure(validation.Message);

        var prepared = validation.Result;
        var admission = await _store.AdmitAsync(
            prepared,
            NodeFeeSettlement.CanonicalizeParentIdempotencyKey(draft!.ParentIdempotencyKey),
            ct);
        if (admission.IsError || admission.Result is null)
        {
            return AZOAResult<NodeFeeSettlement>.Failure(
                $"Node fee settlement unavailable: {admission.Message}");
        }

        var persisted = admission.Result.Settlement;
        if (HasSameImmutableDecision(persisted, prepared))
        {
            return admission.Result.Disposition == NodeFeeSettlementAdmissionDisposition.Created
                ? AZOAResult<NodeFeeSettlement>.Success(persisted, "Settlement prepared.")
                : AZOAResult<NodeFeeSettlement>.Success(persisted, "Settlement already prepared.");
        }

        return AZOAResult<NodeFeeSettlement>.Failure(
            "Node fee settlement conflict: the parent idempotency key already pins a different economic decision.");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeFeeSettlementRecoveryReport>> RecoverDueAsync(
        NodeFeeSettlementRecoveryRequest request,
        CancellationToken ct = default)
    {
        var requestError = ValidateRecoveryRequest(request);
        if (requestError is not null)
            return AZOAResult<NodeFeeSettlementRecoveryReport>.Failure(requestError);

        var candidates = await _store.ListRecoverableAsync(request.Now, request.BatchSize, ct);
        if (candidates.IsError || candidates.Result is null)
        {
            return AZOAResult<NodeFeeSettlementRecoveryReport>.Failure(
                $"Node fee settlement recovery unavailable: {candidates.Message}");
        }

        var claimed = 0;
        var deferred = 0;
        var contended = 0;
        var leaseExpiresAt = request.Now.Add(request.LeaseDuration);
        var nextAttemptAt = request.Now.Add(request.RetryDelay);

        foreach (var candidate in candidates.Result)
        {
            var leaseToken = Guid.NewGuid().ToString("N");
            var claim = await _store.TryClaimRecoveryAsync(
                candidate,
                leaseToken,
                request.Now,
                leaseExpiresAt,
                ct);
            if (claim.IsError)
            {
                return AZOAResult<NodeFeeSettlementRecoveryReport>.Failure(
                    $"Node fee settlement recovery unavailable: {claim.Message}");
            }

            if (claim.Result is null)
            {
                contended++;
                continue;
            }

            claimed++;
            var deferredResult = await _store.TryDeferToReconciliationAsync(
                NodeFeeSettlementRecoveryLease.FromClaim(claim.Result),
                InertRecoveryReason,
                nextAttemptAt,
                request.Now,
                ct);
            if (deferredResult.IsError)
            {
                return AZOAResult<NodeFeeSettlementRecoveryReport>.Failure(
                    $"Node fee settlement recovery unavailable: {deferredResult.Message}");
            }

            if (deferredResult.Result)
                deferred++;
            else
                contended++;
        }

        return AZOAResult<NodeFeeSettlementRecoveryReport>.Success(
            new NodeFeeSettlementRecoveryReport(candidates.Result.Count, claimed, deferred, contended),
            "Settlement recovery sweep completed without chain submission.");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeFeeAtomicGroup?>> RecordAcceptedAtomicGroupAsync(
        NodeFeeSettlementRecoveryLease lease,
        AtomicTransferGroupRequest request,
        AtomicTransferGroupSubmission submission,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (lease is null || request is null || submission is null)
            return AZOAResult<NodeFeeAtomicGroup?>.Failure("A settlement lease, group request, and accepted submission are required.");
        if (now == default)
            return AZOAResult<NodeFeeAtomicGroup?>.Failure("An accepted-group timestamp is required.");
        if (!string.Equals(request.GroupIdentity, submission.GroupIdentity, StringComparison.Ordinal))
            return AZOAResult<NodeFeeAtomicGroup?>.Failure("Accepted group evidence is not bound to the supplied request.");

        var recorded = await _store.TryRecordAcceptedAtomicGroupAsync(
            lease,
            new NodeFeeAcceptedAtomicGroup(request, submission),
            now,
            ct);
        if (recorded.IsError)
        {
            return AZOAResult<NodeFeeAtomicGroup?>.Failure(
                $"Accepted atomic group could not be recorded: {recorded.Message}");
        }

        return recorded.Result is null
            ? AZOAResult<NodeFeeAtomicGroup?>.Success(null, "Accepted atomic group lease contention.")
            : AZOAResult<NodeFeeAtomicGroup?>.Success(recorded.Result, "Accepted atomic group recorded.");
    }

    private const string InertRecoveryReason =
        "Settlement execution is not activated; retained for explicit reconciliation.";

    private static AZOAResult<NodeFeeSettlement> Validate(NodeFeeSettlementDraft? draft)
    {
        if (draft is null)
            return AZOAResult<NodeFeeSettlement>.Failure("Node fee settlement draft is required.");
        if (string.IsNullOrWhiteSpace(draft.ParentIdempotencyKey))
            return AZOAResult<NodeFeeSettlement>.Failure("Parent idempotency key is required.");
        if (!Enum.IsDefined(draft.Operation))
            return AZOAResult<NodeFeeSettlement>.Failure("Node fee operation is not supported.");
        if (string.IsNullOrWhiteSpace(draft.Chain) || !Enum.IsDefined(draft.Network))
            return AZOAResult<NodeFeeSettlement>.Failure("A valid canonical chain and network are required.");
        if (string.IsNullOrWhiteSpace(draft.AssetId))
            return AZOAResult<NodeFeeSettlement>.Failure("Asset id is required.");
        if (string.IsNullOrWhiteSpace(draft.TreasuryAddress))
            return AZOAResult<NodeFeeSettlement>.Failure("Treasury address is required.");
        if (draft.FeeScheduleVersion < 0 || draft.TreasuryDestinationVersion < 0)
            return AZOAResult<NodeFeeSettlement>.Failure("Settlement versions cannot be negative.");
        if (!NodeFeeSettlement.TryParseCanonicalPositiveBaseUnitAmount(draft.GrossAmount, out var gross)
            || !NodeFeeSettlement.TryParseCanonicalPositiveBaseUnitAmount(draft.FeeAmount, out var fee)
            || !NodeFeeSettlement.TryParseCanonicalPositiveBaseUnitAmount(draft.NetAmount, out var net))
            return AZOAResult<NodeFeeSettlement>.Failure("Settlement amounts must be unsigned 64-bit base-unit strings.");
        if (gross == 0 || fee == 0 || net == 0 || (UInt128)fee + net != gross)
            return AZOAResult<NodeFeeSettlement>.Failure("Settlement amounts must be positive and satisfy gross = fee + net.");

        if (!TryCanonicalizeOptionalAtomicGroupIdentity(draft.ExpectedAtomicGroupIdentity, out var expectedAtomicGroupIdentity))
        {
            return AZOAResult<NodeFeeSettlement>.Failure(
                "Expected atomic group identity must be a canonical lowercase SHA-256 digest.");
        }

        var parentKey = NodeFeeSettlement.CanonicalizeParentIdempotencyKey(draft.ParentIdempotencyKey);
        var now = DateTimeOffset.UtcNow;
        return AZOAResult<NodeFeeSettlement>.Success(new NodeFeeSettlement
        {
            Id = NodeFeeSettlement.RecordIdFor(parentKey, draft.Operation.ToString()),
            ParentIdempotencyKeyHash = NodeFeeSettlement.HashParentIdempotencyKey(parentKey),
            Operation = draft.Operation.ToString(),
            Chain = CanonicalizeChain(draft.Chain),
            Network = draft.Network.ToString(),
            AssetId = draft.AssetId.Trim(),
            GrossAmount = gross.ToString(CultureInfo.InvariantCulture),
            FeeAmount = fee.ToString(CultureInfo.InvariantCulture),
            NetAmount = net.ToString(CultureInfo.InvariantCulture),
            FeeScheduleVersion = draft.FeeScheduleVersion,
            TreasuryAddress = draft.TreasuryAddress.Trim(),
            TreasuryDestinationVersion = draft.TreasuryDestinationVersion,
            ExpectedAtomicGroupIdentity = expectedAtomicGroupIdentity,
            State = NodeFeeSettlement.StateKind.Prepared,
            PrimaryEffectState = NodeFeeSettlement.EffectStateKind.NotStarted,
            FeeEffectState = NodeFeeSettlement.EffectStateKind.NotStarted,
            StateVersion = 0,
            AttemptCount = 0,
            NextAttemptAt = now,
            UpdatedAt = now,
        });
    }

    private static string? ValidateRecoveryRequest(NodeFeeSettlementRecoveryRequest? request)
    {
        if (request is null)
            return "Node fee settlement recovery request is required.";
        if (request.BatchSize is < 1 or > 100)
            return "Node fee settlement recovery batch size must be between 1 and 100.";
        if (request.LeaseDuration < TimeSpan.FromSeconds(1)
            || request.LeaseDuration > TimeSpan.FromMinutes(15))
        {
            return "Node fee settlement recovery lease duration must be between one second and fifteen minutes.";
        }

        if (request.RetryDelay < TimeSpan.FromSeconds(1)
            || request.RetryDelay > TimeSpan.FromDays(1))
        {
            return "Node fee settlement recovery retry delay must be between one second and one day.";
        }

        return null;
    }

    private static bool HasSameImmutableDecision(NodeFeeSettlement left, NodeFeeSettlement right)
        => left.ParentIdempotencyKeyHash == right.ParentIdempotencyKeyHash
           && left.Operation == right.Operation
           && left.Chain == right.Chain
           && left.Network == right.Network
           && left.AssetId == right.AssetId
           && left.GrossAmount == right.GrossAmount
           && left.FeeAmount == right.FeeAmount
           && left.NetAmount == right.NetAmount
           && left.FeeScheduleVersion == right.FeeScheduleVersion
           && left.TreasuryAddress == right.TreasuryAddress
           && left.TreasuryDestinationVersion == right.TreasuryDestinationVersion
           && left.ExpectedAtomicGroupIdentity == right.ExpectedAtomicGroupIdentity;

    private static string CanonicalizeChain(string chain)
        => chain.Trim().ToLowerInvariant();

    private static bool TryCanonicalizeOptionalAtomicGroupIdentity(string? value, out string? canonical)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            canonical = null;
            return true;
        }

        canonical = value;
        return string.Equals(value, value.Trim(), StringComparison.Ordinal)
            && canonical.Length == 64
            && canonical.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

}
