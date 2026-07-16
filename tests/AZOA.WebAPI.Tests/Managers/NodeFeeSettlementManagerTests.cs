using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Governance;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Managers;

public sealed class NodeFeeSettlementManagerTests
{
    [Fact]
    public async Task PrepareAsync_PinsTheCompleteEconomicDecision_WithoutEffectIdentifiers()
    {
        var store = new FakeNodeFeeSettlementStore();
        var manager = new NodeFeeSettlementManager(store);

        var result = await manager.PrepareAsync(ValidDraft());

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.State.Should().Be(NodeFeeSettlement.StateKind.Prepared);
        result.Result.PrimaryEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.NotStarted);
        result.Result.FeeEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.NotStarted);
        result.Result.PrimaryOperationId.Should().BeNull();
        result.Result.FeeOperationId.Should().BeNull();
        result.Result.ParentIdempotencyKeyHash.Should().HaveLength(64);
        result.Result.FeeScheduleVersion.Should().Be(7);
        result.Result.TreasuryDestinationVersion.Should().Be(3);
        result.Result.GrossAmount.Should().Be("1000");
        result.Result.FeeAmount.Should().Be("25");
        result.Result.NetAmount.Should().Be("975");
        result.Result.AttemptCount.Should().Be(0);
        result.Result.NextAttemptAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        result.Result.LeaseToken.Should().BeNull();
        result.Result.LeaseExpiresAt.Should().BeNull();
        store.AdmitCount.Should().Be(1);
        store.ParentClaim!.State.Should().Be(IdempotencyState.InProgress);
        store.ParentClaim.ResultPayload.Should().BeNull();
        store.ParentClaim.Error.Should().BeNull();
    }

    [Fact]
    public async Task PrepareAsync_ExactReplayReturnsPreparedRow_WithoutSecondCreate()
    {
        var store = new FakeNodeFeeSettlementStore();
        var manager = new NodeFeeSettlementManager(store);
        var draft = ValidDraft();

        var first = await manager.PrepareAsync(draft);
        var replay = await manager.PrepareAsync(draft);

        replay.IsError.Should().BeFalse(replay.Message);
        replay.Result!.Id.Should().Be(first.Result!.Id);
        replay.Message.Should().Be("Settlement already prepared.");
        store.AdmitCount.Should().Be(2);
    }

    [Fact]
    public async Task PrepareAsync_ConcurrentIdenticalPrepares_ProduceOneCreateAndOneReplay()
    {
        var store = new FakeNodeFeeSettlementStore();
        var manager = new NodeFeeSettlementManager(store);

        var results = await Task.WhenAll(
            manager.PrepareAsync(ValidDraft()),
            manager.PrepareAsync(ValidDraft()));

        results.Should().OnlyContain(result => !result.IsError, string.Join(Environment.NewLine, results.Select(result => result.Message)));
        results.Select(result => result.Result!.Id).Distinct().Should().ContainSingle();
        results.Select(result => result.Message).Should().Contain("Settlement prepared.");
        results.Select(result => result.Message).Should().Contain("Settlement already prepared.");
        store.AdmitCount.Should().Be(2);
    }

    [Fact]
    public async Task PrepareAsync_DifferentDecisionForSameParentAndOperation_RejectsWithoutOverwrite()
    {
        var store = new FakeNodeFeeSettlementStore();
        var manager = new NodeFeeSettlementManager(store);
        var original = ValidDraft();
        await manager.PrepareAsync(original);

        var result = await manager.PrepareAsync(new NodeFeeSettlementDraft
        {
            ParentIdempotencyKey = original.ParentIdempotencyKey,
            Operation = original.Operation,
            Chain = original.Chain,
            Network = original.Network,
            AssetId = original.AssetId,
            GrossAmount = "1000",
            FeeAmount = "30",
            NetAmount = "970",
            FeeScheduleVersion = original.FeeScheduleVersion,
            TreasuryAddress = original.TreasuryAddress,
            TreasuryDestinationVersion = original.TreasuryDestinationVersion,
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("conflict");
        store.AdmitCount.Should().Be(2);
    }

    [Fact]
    public async Task PrepareAsync_UnbalancedAmounts_RejectsBeforeStoreAccess()
    {
        var store = new FakeNodeFeeSettlementStore();
        var manager = new NodeFeeSettlementManager(store);

        var result = await manager.PrepareAsync(new NodeFeeSettlementDraft
        {
            ParentIdempotencyKey = "payment-intent-1",
            Operation = NodeFeeOperation.Transfer,
            Chain = "Algorand",
            Network = AZOA.WebAPI.Core.ChainNetwork.Mainnet,
            AssetId = "42",
            GrossAmount = "1000",
            FeeAmount = "25",
            NetAmount = "974",
            FeeScheduleVersion = 7,
            TreasuryAddress = "TREASURY",
            TreasuryDestinationVersion = 3,
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("gross = fee + net");
        store.AdmitCount.Should().Be(0);
    }

    [Theory]
    [InlineData("0", "25", "975")]
    [InlineData("01000", "25", "975")]
    [InlineData("18446744073709551616", "1", "18446744073709551615")]
    public async Task PrepareAsync_NonCanonicalOrOutOfRangeAmounts_RejectBeforeStoreAccess(
        string gross,
        string fee,
        string net)
    {
        var store = new FakeNodeFeeSettlementStore();
        var manager = new NodeFeeSettlementManager(store);

        var result = await manager.PrepareAsync(ValidDraft(gross, fee, net));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("unsigned 64-bit");
        store.AdmitCount.Should().Be(0);
    }

    [Fact]
    public async Task PrepareAsync_UnconfirmedAdmissionFailure_IsNotNormalizedAsReplay()
    {
        var store = new FakeNodeFeeSettlementStore { AdmissionFailureMessage = "schema assertion failed" };
        var manager = new NodeFeeSettlementManager(store);

        var result = await manager.PrepareAsync(ValidDraft());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("schema assertion failed");
        store.AdmitCount.Should().Be(1);
    }

    [Fact]
    public async Task PrepareAsync_ExistingNormalIdempotencyClaim_FailsClosedWithoutSettlementAdmission()
    {
        var store = new FakeNodeFeeSettlementStore();
        var manager = new NodeFeeSettlementManager(store);
        var draft = ValidDraft();
        store.SeedOuterClaim(draft.ParentIdempotencyKey, "allocation/mint");

        var result = await manager.PrepareAsync(draft);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("already claimed");
        store.AdmitCount.Should().Be(1);
        store.ParentClaim!.OperationType.Should().Be("allocation/mint");
        store.SettlementCount.Should().Be(0);
    }

    [Fact]
    public async Task RecoverDueAsync_ClaimsAndDefersWithoutChangingEitherEffect()
    {
        var store = new FakeNodeFeeSettlementStore();
        var manager = new NodeFeeSettlementManager(store);
        var prepared = (await manager.PrepareAsync(ValidDraft())).Result!;
        var now = DateTimeOffset.UtcNow.AddMinutes(1);

        var result = await manager.RecoverDueAsync(RecoveryRequest(now));
        var persisted = (await store.GetAsync(prepared.Id)).Result!;

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Be(new NodeFeeSettlementRecoveryReport(1, 1, 1, 0));
        persisted.State.Should().Be(NodeFeeSettlement.StateKind.AwaitingReconciliation);
        persisted.PrimaryEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.NotStarted);
        persisted.FeeEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.NotStarted);
        persisted.PrimaryOperationId.Should().BeNull();
        persisted.FeeOperationId.Should().BeNull();
        persisted.LeaseToken.Should().BeNull();
        persisted.LeaseExpiresAt.Should().BeNull();
        persisted.AttemptCount.Should().Be(1);
        persisted.StateVersion.Should().Be(2);
        persisted.NextAttemptAt.Should().Be(now.AddMinutes(5));
        persisted.ReconciliationReason.Should().Contain("not activated");
    }

    [Fact]
    public async Task RecoverDueAsync_ConcurrentSweepsHaveOneLeaseWinner()
    {
        var store = new FakeNodeFeeSettlementStore(blockFirstTwoRecoveryScans: true);
        var manager = new NodeFeeSettlementManager(store);
        await manager.PrepareAsync(ValidDraft());
        var now = DateTimeOffset.UtcNow.AddMinutes(1);

        var reports = await Task.WhenAll(
            manager.RecoverDueAsync(RecoveryRequest(now)),
            manager.RecoverDueAsync(RecoveryRequest(now)));

        reports.Should().OnlyContain(report => !report.IsError, string.Join(Environment.NewLine, reports.Select(report => report.Message)));
        reports.Sum(report => report.Result!.Claimed).Should().Be(1);
        reports.Sum(report => report.Result!.Deferred).Should().Be(1);
        reports.Sum(report => report.Result!.Contended).Should().Be(1);
    }

    [Fact]
    public async Task RecoverDueAsync_ReclaimsAnExpiredLeaseEvenWhenRetryIsNotYetDue()
    {
        var store = new FakeNodeFeeSettlementStore();
        var manager = new NodeFeeSettlementManager(store);
        var prepared = (await manager.PrepareAsync(ValidDraft())).Result!;
        var now = DateTimeOffset.UtcNow.AddMinutes(2);
        store.Mutate(prepared.Id, settlement =>
        {
            settlement.State = NodeFeeSettlement.StateKind.PrimarySubmitted;
            settlement.PrimaryEffectState = NodeFeeSettlement.EffectStateKind.Submitted;
            settlement.LeaseToken = "expired-worker";
            settlement.LeaseExpiresAt = now.AddSeconds(-1);
            settlement.NextAttemptAt = now.AddHours(1);
            settlement.AttemptCount = 4;
            settlement.StateVersion = 9;
        });

        var result = await manager.RecoverDueAsync(RecoveryRequest(now));
        var persisted = (await store.GetAsync(prepared.Id)).Result!;

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Be(new NodeFeeSettlementRecoveryReport(1, 1, 1, 0));
        persisted.State.Should().Be(NodeFeeSettlement.StateKind.AwaitingReconciliation);
        persisted.PrimaryEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.Submitted);
        persisted.AttemptCount.Should().Be(5);
        persisted.StateVersion.Should().Be(11);
        persisted.LeaseToken.Should().BeNull();
    }

    [Fact]
    public async Task PairedTerminalization_ConfirmsBothDistinctEffectsAndCompletesParentTogether()
    {
        var store = new FakeNodeFeeSettlementStore();
        var manager = new NodeFeeSettlementManager(store);
        var prepared = (await manager.PrepareAsync(ValidDraft())).Result!;
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        var claim = await store.TryClaimRecoveryAsync(prepared, "terminal-worker", now, now.AddMinutes(1));

        var settled = await store.TrySettlePairedAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(claim.Result!),
            new NodeFeeSettlementTerminalization("payment-intent-1", "primary:tx-1", "fee:tx-2", "{\"settled\":true}"),
            now.AddSeconds(1));
        var persisted = (await store.GetAsync(prepared.Id)).Result!;

        settled.IsError.Should().BeFalse(settled.Message);
        settled.Result.Should().BeTrue();
        persisted.State.Should().Be(NodeFeeSettlement.StateKind.Settled);
        persisted.PrimaryEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.Confirmed);
        persisted.FeeEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.Confirmed);
        persisted.PrimaryTransactionHash.Should().Be("primary:tx-1");
        persisted.FeeTransactionHash.Should().Be("fee:tx-2");
        persisted.LeaseToken.Should().BeNull();
        store.ParentClaim!.State.Should().Be(IdempotencyState.Completed);
        store.ParentClaim.ResultPayload.Should().Be("{\"settled\":true}");
    }

    [Fact]
    public async Task PairedTerminalization_RejectsSameEffectReferenceWithoutChangingEitherRow()
    {
        var store = new FakeNodeFeeSettlementStore();
        var manager = new NodeFeeSettlementManager(store);
        var prepared = (await manager.PrepareAsync(ValidDraft())).Result!;
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        var claim = await store.TryClaimRecoveryAsync(prepared, "terminal-worker", now, now.AddMinutes(1));

        Func<Task> act = async () => await store.TrySettlePairedAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(claim.Result!),
            new NodeFeeSettlementTerminalization("payment-intent-1", "same:tx", "same:tx", "payload"),
            now.AddSeconds(1));
        var persisted = (await store.GetAsync(prepared.Id)).Result!;

        await act.Should().ThrowAsync<ArgumentException>();
        persisted.State.Should().Be(NodeFeeSettlement.StateKind.Prepared);
        persisted.PrimaryEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.NotStarted);
        persisted.FeeEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.NotStarted);
        store.ParentClaim!.State.Should().Be(IdempotencyState.InProgress);
    }

    [Fact]
    public async Task NonterminalReconciliation_RecordsUnknownOrFailedEffectsWithoutTerminalizingParent()
    {
        var store = new FakeNodeFeeSettlementStore();
        var manager = new NodeFeeSettlementManager(store);
        var prepared = (await manager.PrepareAsync(ValidDraft())).Result!;
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        var claim = await store.TryClaimRecoveryAsync(prepared, "reconcile-worker", now, now.AddMinutes(1));

        var deferred = await store.TryRecordNonTerminalReconciliationAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(claim.Result!),
            new NodeFeeSettlementEffectReconciliation(
                NodeFeeSettlement.EffectStateKind.Unknown,
                "primary:ambiguous",
                NodeFeeSettlement.EffectStateKind.Failed,
                "fee:rejected"),
            "chain status remains unresolved",
            now.AddMinutes(5),
            now.AddSeconds(1));
        var persisted = (await store.GetAsync(prepared.Id)).Result!;

        deferred.Result.Should().BeTrue();
        persisted.State.Should().Be(NodeFeeSettlement.StateKind.AwaitingReconciliation);
        persisted.PrimaryEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.Unknown);
        persisted.FeeEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.Failed);
        store.ParentClaim!.State.Should().Be(IdempotencyState.InProgress);
    }

    [Fact]
    public async Task PairedTerminalization_StaleLeaseAndReverseTerminalAttemptCannotChangeCompletedPair()
    {
        var store = new FakeNodeFeeSettlementStore();
        var manager = new NodeFeeSettlementManager(store);
        var prepared = (await manager.PrepareAsync(ValidDraft())).Result!;
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        var first = await store.TryClaimRecoveryAsync(prepared, "old-worker", now, now.AddSeconds(1));
        var current = await store.TryClaimRecoveryAsync(first.Result!, "current-worker", now.AddSeconds(2), now.AddMinutes(1));
        var terminalization = new NodeFeeSettlementTerminalization(
            "payment-intent-1", "primary:tx-1", "fee:tx-2", "payload");

        var stale = await store.TrySettlePairedAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(first.Result!), terminalization, now.AddSeconds(3));
        var settled = await store.TrySettlePairedAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(current.Result!), terminalization, now.AddSeconds(3));
        var reverse = await store.TryDeferToReconciliationAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(current.Result!),
            "must not reverse a terminal pair",
            now.AddMinutes(5),
            now.AddSeconds(4));
        var persisted = (await store.GetAsync(prepared.Id)).Result!;

        stale.Result.Should().BeFalse();
        settled.Result.Should().BeTrue();
        reverse.Result.Should().BeFalse();
        persisted.State.Should().Be(NodeFeeSettlement.StateKind.Settled);
        store.ParentClaim!.State.Should().Be(IdempotencyState.Completed);
    }

    [Fact]
    public async Task ConfirmedEffectReference_CannotBeReplacedBeforePairedTerminalization()
    {
        var store = new FakeNodeFeeSettlementStore();
        var manager = new NodeFeeSettlementManager(store);
        var prepared = (await manager.PrepareAsync(ValidDraft())).Result!;
        var now = DateTimeOffset.UtcNow;
        var firstClaim = await store.TryClaimRecoveryAsync(
            prepared, "first-worker", now, now.AddSeconds(2));
        var recorded = await store.TryRecordNonTerminalReconciliationAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(firstClaim.Result!),
            new NodeFeeSettlementEffectReconciliation(
                NodeFeeSettlement.EffectStateKind.Confirmed, "primary:observed",
                NodeFeeSettlement.EffectStateKind.Failed, "fee:failed"),
            "fee effect requires reconciliation", now.AddSeconds(1), now);

        recorded.Result.Should().BeTrue();
        var secondClaim = await store.TryClaimRecoveryAsync(
            (await store.GetAsync(prepared.Id)).Result!,
            "second-worker", now.AddSeconds(2), now.AddSeconds(3));
        var conflicting = await store.TrySettlePairedAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(secondClaim.Result!),
            new NodeFeeSettlementTerminalization(
                "payment-intent-1", "primary:replacement", "fee:confirmed", "payload"),
            now.AddSeconds(2));

        conflicting.Result.Should().BeFalse();
        var afterConflict = (await store.GetAsync(prepared.Id)).Result!;
        afterConflict.PrimaryEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.Confirmed);
        afterConflict.PrimaryTransactionHash.Should().Be("primary:observed");

        var finalClaim = await store.TryClaimRecoveryAsync(
            afterConflict, "final-worker", now.AddSeconds(4), now.AddMinutes(1));
        var settled = await store.TrySettlePairedAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(finalClaim.Result!),
            new NodeFeeSettlementTerminalization(
                "payment-intent-1", "primary:observed", "fee:confirmed", "payload"),
            now.AddSeconds(4));

        settled.Result.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 30, 300)]
    [InlineData(101, 30, 300)]
    [InlineData(1, 0, 300)]
    [InlineData(1, 30, 0)]
    public async Task RecoverDueAsync_InvalidRequest_RejectsBeforeStoreAccess(
        int batchSize,
        int leaseSeconds,
        int retrySeconds)
    {
        var store = new FakeNodeFeeSettlementStore();
        var manager = new NodeFeeSettlementManager(store);

        var result = await manager.RecoverDueAsync(new NodeFeeSettlementRecoveryRequest(
            DateTimeOffset.UtcNow,
            batchSize,
            TimeSpan.FromSeconds(leaseSeconds),
            TimeSpan.FromSeconds(retrySeconds)));

        result.IsError.Should().BeTrue();
        store.RecoveryScanCount.Should().Be(0);
    }

    private static NodeFeeSettlementDraft ValidDraft(
        string grossAmount = "1000",
        string feeAmount = "25",
        string netAmount = "975") => new()
    {
        ParentIdempotencyKey = "payment-intent-1",
        Operation = NodeFeeOperation.Transfer,
        Chain = "Algorand",
        Network = AZOA.WebAPI.Core.ChainNetwork.Mainnet,
        AssetId = "42",
        GrossAmount = grossAmount,
        FeeAmount = feeAmount,
        NetAmount = netAmount,
        FeeScheduleVersion = 7,
        TreasuryAddress = "TREASURY",
        TreasuryDestinationVersion = 3,
    };

    private static NodeFeeSettlementRecoveryRequest RecoveryRequest(DateTimeOffset now) => new(
        now,
        BatchSize: 10,
        LeaseDuration: TimeSpan.FromSeconds(30),
        RetryDelay: TimeSpan.FromMinutes(5));

    private sealed class FakeNodeFeeSettlementStore : INodeFeeSettlementStore
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, NodeFeeSettlement> _rows = new(StringComparer.Ordinal);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IdempotencyRecord> _parentClaims = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _firstTwoReadsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _blockFirstTwoReads;
        private readonly bool _blockFirstTwoRecoveryScans;
        private readonly TaskCompletionSource _firstTwoRecoveryScansReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _getCount;
        private int _admitCount;
        private int _recoveryScanCount;
        private readonly object _recoveryGate = new();

        public FakeNodeFeeSettlementStore(
            bool blockFirstTwoReads = false,
            bool blockFirstTwoRecoveryScans = false)
        {
            _blockFirstTwoReads = blockFirstTwoReads;
            _blockFirstTwoRecoveryScans = blockFirstTwoRecoveryScans;
        }

        public int GetCount => _getCount;

        public int AdmitCount => _admitCount;

        public int RecoveryScanCount => _recoveryScanCount;

        public string? AdmissionFailureMessage { get; init; }

        public IdempotencyRecord? ParentClaim => _parentClaims.Values.SingleOrDefault();

        public int SettlementCount => _rows.Count;

        public void SeedOuterClaim(string parentIdempotencyKey, string operationType)
        {
            var canonicalKey = NodeFeeSettlement.CanonicalizeParentIdempotencyKey(parentIdempotencyKey);
            _parentClaims[NodeFeeSettlement.HashParentIdempotencyKey(canonicalKey)] = new IdempotencyRecord
            {
                Key = canonicalKey,
                OperationType = operationType,
                State = IdempotencyState.InProgress,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
        }

        public Task<AZOAResult<NodeFeeSettlementAdmissionResult>> AdmitAsync(
            NodeFeeSettlement settlement,
            string parentIdempotencyKey,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _admitCount);
            if (AdmissionFailureMessage is not null)
            {
                return Task.FromResult(AZOAResult<NodeFeeSettlementAdmissionResult>.Failure(
                    AdmissionFailureMessage));
            }

            lock (_recoveryGate)
            {
                var canonicalKey = NodeFeeSettlement.CanonicalizeParentIdempotencyKey(parentIdempotencyKey);
                var parentId = NodeFeeSettlement.HashParentIdempotencyKey(canonicalKey);
                var hasParent = _parentClaims.TryGetValue(parentId, out var parent);
                var hasSettlement = _rows.TryGetValue(settlement.Id, out var persisted);
                if (hasParent && !hasSettlement)
                {
                    if (!string.Equals(parent!.OperationType,
                        NodeFeeSettlement.ParentClaimOperationType(settlement.Operation),
                        StringComparison.Ordinal))
                    {
                        return Task.FromResult(AZOAResult<NodeFeeSettlementAdmissionResult>.Failure(
                            "Parent idempotency key is already claimed by another operation."));
                    }

                    return Task.FromResult(AZOAResult<NodeFeeSettlementAdmissionResult>.Failure(
                        "Node fee settlement admission is inconsistent."));
                }

                if (!hasParent && hasSettlement)
                {
                    return Task.FromResult(AZOAResult<NodeFeeSettlementAdmissionResult>.Failure(
                        "Node fee settlement admission is inconsistent."));
                }

                if (!hasParent)
                {
                    parent = new IdempotencyRecord
                    {
                        Key = canonicalKey,
                        OperationType = NodeFeeSettlement.ParentClaimOperationType(settlement.Operation),
                        State = IdempotencyState.InProgress,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    };
                    _parentClaims[parentId] = parent;
                    _rows[settlement.Id] = settlement;
                    persisted = settlement;
                    return Task.FromResult(AZOAResult<NodeFeeSettlementAdmissionResult>.Success(
                        new NodeFeeSettlementAdmissionResult(
                            persisted,
                            parent,
                            NodeFeeSettlementAdmissionDisposition.Created)));
                }

                if (!string.Equals(parent!.Key, canonicalKey, StringComparison.Ordinal)
                    || !string.Equals(parent.OperationType,
                        NodeFeeSettlement.ParentClaimOperationType(settlement.Operation),
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(AZOAResult<NodeFeeSettlementAdmissionResult>.Failure(
                        "Node fee settlement parent claim conflict."));
                }

                return Task.FromResult(AZOAResult<NodeFeeSettlementAdmissionResult>.Success(
                    new NodeFeeSettlementAdmissionResult(
                        persisted!,
                        parent,
                        NodeFeeSettlementAdmissionDisposition.Replayed)));
            }
        }

        public async Task<AZOAResult<NodeFeeSettlement?>> GetAsync(string settlementId, CancellationToken ct = default)
        {
            var getCount = Interlocked.Increment(ref _getCount);
            _rows.TryGetValue(settlementId, out var settlement);
            if (_blockFirstTwoReads && getCount <= 2)
            {
                if (getCount == 2)
                    _firstTwoReadsReady.TrySetResult();

                await _firstTwoReadsReady.Task.WaitAsync(ct);
            }

            return AZOAResult<NodeFeeSettlement?>.Success(settlement);
        }

        public async Task<AZOAResult<IReadOnlyList<NodeFeeSettlement>>> ListRecoverableAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken ct = default)
        {
            var scanCount = Interlocked.Increment(ref _recoveryScanCount);
            IReadOnlyList<NodeFeeSettlement> rows;
            lock (_recoveryGate)
            {
                rows = _rows.Values
                    .Where(settlement => IsRecoverable(settlement)
                                       && ((settlement.LeaseToken is null && settlement.NextAttemptAt <= now)
                                           || settlement.LeaseExpiresAt <= now))
                    .OrderBy(settlement => settlement.NextAttemptAt)
                    .ThenBy(settlement => settlement.Id, StringComparer.Ordinal)
                    .Take(batchSize)
                    .Select(Clone)
                    .ToArray();
            }

            if (_blockFirstTwoRecoveryScans && scanCount <= 2)
            {
                if (scanCount == 2)
                    _firstTwoRecoveryScansReady.TrySetResult();

                await _firstTwoRecoveryScansReady.Task.WaitAsync(ct);
            }

            return AZOAResult<IReadOnlyList<NodeFeeSettlement>>.Success(rows);
        }

        public Task<AZOAResult<NodeFeeSettlement?>> TryClaimRecoveryAsync(
            NodeFeeSettlement candidate,
            string leaseToken,
            DateTimeOffset now,
            DateTimeOffset leaseExpiresAt,
            CancellationToken ct = default)
        {
            lock (_recoveryGate)
            {
                if (!_rows.TryGetValue(candidate.Id, out var stored)
                    || stored.StateVersion != candidate.StateVersion
                    || !IsRecoverable(stored)
                    || !((stored.LeaseToken is null && stored.NextAttemptAt <= now)
                         || stored.LeaseExpiresAt <= now))
                {
                    return Task.FromResult(AZOAResult<NodeFeeSettlement?>.Success(null));
                }

                stored.LeaseToken = leaseToken;
                stored.LeaseExpiresAt = leaseExpiresAt;
                stored.AttemptCount++;
                stored.StateVersion++;
                stored.UpdatedAt = now;
                return Task.FromResult(AZOAResult<NodeFeeSettlement?>.Success(Clone(stored)));
            }
        }

        public Task<AZOAResult<bool>> TryDeferToReconciliationAsync(
            NodeFeeSettlementRecoveryLease lease,
            string reason,
            DateTimeOffset nextAttemptAt,
            DateTimeOffset now,
            CancellationToken ct = default)
        {
            lock (_recoveryGate)
            {
                if (!_rows.TryGetValue(lease.SettlementId, out var stored)
                    || stored.StateVersion != lease.StateVersion
                    || stored.LeaseToken != lease.LeaseToken
                    || stored.LeaseExpiresAt <= now)
                {
                    return Task.FromResult(AZOAResult<bool>.Success(false));
                }

                stored.State = NodeFeeSettlement.StateKind.AwaitingReconciliation;
                stored.StateVersion++;
                stored.ReconciliationReason = reason;
                stored.NextAttemptAt = nextAttemptAt;
                stored.LeaseToken = null;
                stored.LeaseExpiresAt = null;
                stored.UpdatedAt = now;
                return Task.FromResult(AZOAResult<bool>.Success(true));
            }
        }

        public Task<AZOAResult<bool>> TryRecordNonTerminalReconciliationAsync(
            NodeFeeSettlementRecoveryLease lease,
            NodeFeeSettlementEffectReconciliation reconciliation,
            string reason,
            DateTimeOffset nextAttemptAt,
            DateTimeOffset now,
            CancellationToken ct = default)
        {
            lock (_recoveryGate)
            {
                if (!_rows.TryGetValue(lease.SettlementId, out var stored)
                    || stored.StateVersion != lease.StateVersion
                    || stored.LeaseToken != lease.LeaseToken
                    || stored.LeaseExpiresAt <= now
                    || !reconciliation.IsNonTerminal
                    || !PreservesConfirmedEffect(stored.PrimaryEffectState, stored.PrimaryTransactionHash,
                        reconciliation.PrimaryEffectState, reconciliation.PrimaryEffectReference)
                    || !PreservesConfirmedEffect(stored.FeeEffectState, stored.FeeTransactionHash,
                        reconciliation.FeeEffectState, reconciliation.FeeEffectReference))
                {
                    return Task.FromResult(AZOAResult<bool>.Success(false));
                }

                stored.State = NodeFeeSettlement.StateKind.AwaitingReconciliation;
                stored.PrimaryEffectState = reconciliation.PrimaryEffectState;
                stored.PrimaryTransactionHash = string.IsNullOrWhiteSpace(reconciliation.PrimaryEffectReference)
                    ? null : reconciliation.PrimaryEffectReference.Trim();
                stored.FeeEffectState = reconciliation.FeeEffectState;
                stored.FeeTransactionHash = string.IsNullOrWhiteSpace(reconciliation.FeeEffectReference)
                    ? null : reconciliation.FeeEffectReference.Trim();
                stored.StateVersion++;
                stored.ReconciliationReason = reason;
                stored.NextAttemptAt = nextAttemptAt;
                stored.LeaseToken = null;
                stored.LeaseExpiresAt = null;
                stored.UpdatedAt = now;
                return Task.FromResult(AZOAResult<bool>.Success(true));
            }
        }

        public Task<AZOAResult<bool>> TrySettlePairedAsync(
            NodeFeeSettlementRecoveryLease lease,
            NodeFeeSettlementTerminalization terminalization,
            DateTimeOffset now,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(terminalization);
            ArgumentException.ThrowIfNullOrWhiteSpace(terminalization.ParentIdempotencyKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(terminalization.PrimaryEffectReference);
            ArgumentException.ThrowIfNullOrWhiteSpace(terminalization.FeeEffectReference);
            ArgumentException.ThrowIfNullOrWhiteSpace(terminalization.ParentResultPayload);
            if (string.Equals(terminalization.PrimaryEffectReference.Trim(), terminalization.FeeEffectReference.Trim(), StringComparison.Ordinal))
                throw new ArgumentException("Confirmed primary and fee effects must have distinct references.", nameof(terminalization));

            lock (_recoveryGate)
            {
                if (!_rows.TryGetValue(lease.SettlementId, out var stored)
                    || stored.StateVersion != lease.StateVersion
                    || stored.LeaseToken != lease.LeaseToken
                    || stored.LeaseExpiresAt <= now
                    || !IsRecoverable(stored)
                    || string.IsNullOrWhiteSpace(terminalization.PrimaryEffectReference)
                    || string.IsNullOrWhiteSpace(terminalization.FeeEffectReference)
                    || !PreservesConfirmedEffect(stored.PrimaryEffectState, stored.PrimaryTransactionHash,
                        NodeFeeSettlement.EffectStateKind.Confirmed, terminalization.PrimaryEffectReference)
                    || !PreservesConfirmedEffect(stored.FeeEffectState, stored.FeeTransactionHash,
                        NodeFeeSettlement.EffectStateKind.Confirmed, terminalization.FeeEffectReference))
                {
                    return Task.FromResult(AZOAResult<bool>.Success(false));
                }

                var parentKey = NodeFeeSettlement.CanonicalizeParentIdempotencyKey(terminalization.ParentIdempotencyKey);
                var parentId = NodeFeeSettlement.HashParentIdempotencyKey(parentKey);
                if (!_parentClaims.TryGetValue(parentId, out var parent)
                    || parent.State != IdempotencyState.InProgress
                    || parent.OperationType != NodeFeeSettlement.ParentClaimOperationType(stored.Operation))
                {
                    return Task.FromResult(AZOAResult<bool>.Success(false));
                }

                stored.State = NodeFeeSettlement.StateKind.Settled;
                stored.PrimaryEffectState = NodeFeeSettlement.EffectStateKind.Confirmed;
                stored.PrimaryTransactionHash = terminalization.PrimaryEffectReference.Trim();
                stored.FeeEffectState = NodeFeeSettlement.EffectStateKind.Confirmed;
                stored.FeeTransactionHash = terminalization.FeeEffectReference.Trim();
                stored.StateVersion++;
                stored.ReconciliationReason = null;
                stored.LeaseToken = null;
                stored.LeaseExpiresAt = null;
                stored.UpdatedAt = now;
                parent.State = IdempotencyState.Completed;
                parent.ResultPayload = terminalization.ParentResultPayload.Trim();
                parent.Error = null;
                parent.UpdatedAt = now.UtcDateTime;
                return Task.FromResult(AZOAResult<bool>.Success(true));
            }
        }

        public void Mutate(string settlementId, Action<NodeFeeSettlement> mutate)
        {
            lock (_recoveryGate)
            {
                mutate(_rows[settlementId]);
            }
        }

        private static bool IsRecoverable(NodeFeeSettlement settlement)
            => settlement.State is NodeFeeSettlement.StateKind.Prepared
                or NodeFeeSettlement.StateKind.PrimarySubmitted
                or NodeFeeSettlement.StateKind.FeeSubmitted
                or NodeFeeSettlement.StateKind.AwaitingReconciliation;

        private static bool PreservesConfirmedEffect(
            NodeFeeSettlement.EffectStateKind storedState,
            string? storedReference,
            NodeFeeSettlement.EffectStateKind incomingState,
            string? incomingReference)
            => storedState != NodeFeeSettlement.EffectStateKind.Confirmed
               || (incomingState == NodeFeeSettlement.EffectStateKind.Confirmed
                   && string.Equals(storedReference, incomingReference?.Trim(), StringComparison.Ordinal));

        private static NodeFeeSettlement Clone(NodeFeeSettlement settlement) => new()
        {
            Id = settlement.Id,
            ParentIdempotencyKeyHash = settlement.ParentIdempotencyKeyHash,
            Operation = settlement.Operation,
            Chain = settlement.Chain,
            Network = settlement.Network,
            AssetId = settlement.AssetId,
            GrossAmount = settlement.GrossAmount,
            FeeAmount = settlement.FeeAmount,
            NetAmount = settlement.NetAmount,
            FeeScheduleVersion = settlement.FeeScheduleVersion,
            TreasuryAddress = settlement.TreasuryAddress,
            TreasuryDestinationVersion = settlement.TreasuryDestinationVersion,
            State = settlement.State,
            PrimaryEffectState = settlement.PrimaryEffectState,
            FeeEffectState = settlement.FeeEffectState,
            PrimaryOperationId = settlement.PrimaryOperationId,
            FeeOperationId = settlement.FeeOperationId,
            PrimaryTransactionHash = settlement.PrimaryTransactionHash,
            FeeTransactionHash = settlement.FeeTransactionHash,
            StateVersion = settlement.StateVersion,
            AttemptCount = settlement.AttemptCount,
            NextAttemptAt = settlement.NextAttemptAt,
            LeaseToken = settlement.LeaseToken,
            LeaseExpiresAt = settlement.LeaseExpiresAt,
            ReconciliationReason = settlement.ReconciliationReason,
            CreatedAt = settlement.CreatedAt,
            UpdatedAt = settlement.UpdatedAt,
        };
    }
}
