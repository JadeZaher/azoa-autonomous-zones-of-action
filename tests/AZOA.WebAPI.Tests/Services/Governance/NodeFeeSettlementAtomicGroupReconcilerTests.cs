using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Governance;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Services.Governance;

public sealed class NodeFeeSettlementAtomicGroupReconcilerTests
{
    [Fact]
    public async Task ReconcileDueAsync_ExactConfirmedReceipt_SettlesWithHashOnlyParentProofAndNeverBroadcasts()
    {
        const string rawParentKey = "purchase:tenant:asset:001";
        var settlement = Settlement(NodeFeeSettlement.HashParentIdempotencyKey(rawParentKey));
        var receipt = ReceiptFor(settlement);
        var observation = ConfirmedObservation(receipt, confirmedRound: 42);
        var store = NewStore(settlement, receipt);
        var observer = NewObserver(observation);
        var providers = NewProviders(observer.Object);
        NodeFeeSettlementTerminalization? terminalization = null;

        store.Setup(s => s.TrySettlePairedAsync(
                It.IsAny<NodeFeeSettlementRecoveryLease>(),
                It.IsAny<NodeFeeSettlementTerminalization>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .Callback((NodeFeeSettlementRecoveryLease lease, NodeFeeSettlementTerminalization value,
                DateTimeOffset now, CancellationToken token) => terminalization = value)
            .ReturnsAsync(AZOAResult<bool>.Success(true));

        var result = await new NodeFeeSettlementAtomicGroupReconciler(store.Object, providers.Object)
            .ReconcileDueAsync(Request());

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Be(new NodeFeeSettlementAtomicGroupReconciliationReport(1, 1, 1, 0, 0, 0));
        terminalization.Should().NotBeNull();
        terminalization!.ParentIdempotencyKey.Should().BeNull();
        terminalization.ParentIdempotencyKeyHash.Should().Be(settlement.ParentIdempotencyKeyHash);
        terminalization.PrimaryEffectReference.Should().Be(receipt.PrimaryTransactionId);
        terminalization.FeeEffectReference.Should().Be(receipt.TreasuryTransactionId);
        terminalization.ParentResultPayload.Should().NotContain(rawParentKey);
        terminalization.ParentResultPayload.Should().Contain("\"confirmedRound\":42");
        observer.Verify(o => o.ObserveAtomicTransferGroupAsync(
            It.IsAny<AtomicTransferGroupObservationEvidence>(), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.TryRecordAcceptedAtomicGroupAsync(
            It.IsAny<NodeFeeSettlementRecoveryLease>(),
            It.IsAny<NodeFeeAcceptedAtomicGroup>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(s => s.TryRecordNonTerminalReconciliationAsync(
            It.IsAny<NodeFeeSettlementRecoveryLease>(),
            It.IsAny<NodeFeeSettlementEffectReconciliation>(),
            It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(AtomicTransferGroupObservationVerdict.Incomplete, AtomicTransferLegObservationVerdict.Pending,
        NodeFeeSettlement.EffectStateKind.Unknown)]
    [InlineData(AtomicTransferGroupObservationVerdict.Unavailable, AtomicTransferLegObservationVerdict.Unavailable,
        NodeFeeSettlement.EffectStateKind.Unknown)]
    [InlineData(AtomicTransferGroupObservationVerdict.Mismatched, AtomicTransferLegObservationVerdict.Mismatched,
        NodeFeeSettlement.EffectStateKind.Failed)]
    [InlineData(AtomicTransferGroupObservationVerdict.Rejected, AtomicTransferLegObservationVerdict.PoolRejected,
        NodeFeeSettlement.EffectStateKind.Failed)]
    public async Task ReconcileDueAsync_NonterminalObservation_RecordsEffectsWithoutTerminalization(
        AtomicTransferGroupObservationVerdict verdict,
        AtomicTransferLegObservationVerdict legVerdict,
        NodeFeeSettlement.EffectStateKind expectedEffectState)
    {
        var settlement = Settlement();
        var receipt = ReceiptFor(settlement);
        var observation = Observation(receipt, verdict, legVerdict);
        var store = NewStore(settlement, receipt);
        var observer = NewObserver(observation);
        var providers = NewProviders(observer.Object);
        NodeFeeSettlementEffectReconciliation? reconciliation = null;

        store.Setup(s => s.TryRecordNonTerminalReconciliationAsync(
                It.IsAny<NodeFeeSettlementRecoveryLease>(),
                It.IsAny<NodeFeeSettlementEffectReconciliation>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .Callback((NodeFeeSettlementRecoveryLease lease, NodeFeeSettlementEffectReconciliation value,
                string reason, DateTimeOffset nextAttemptAt, DateTimeOffset now, CancellationToken token) => reconciliation = value)
            .ReturnsAsync(AZOAResult<bool>.Success(true));

        var result = await new NodeFeeSettlementAtomicGroupReconciler(store.Object, providers.Object)
            .ReconcileDueAsync(Request());

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Be(new NodeFeeSettlementAtomicGroupReconciliationReport(1, 1, 0, 1, 0, 0));
        reconciliation.Should().NotBeNull();
        reconciliation!.PrimaryEffectState.Should().Be(expectedEffectState);
        reconciliation.FeeEffectState.Should().Be(expectedEffectState);
        store.Verify(s => s.TrySettlePairedAsync(
            It.IsAny<NodeFeeSettlementRecoveryLease>(),
            It.IsAny<NodeFeeSettlementTerminalization>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileDueAsync_ReceiptDisappearsAfterAcceptedClaim_DefersWithoutResolvingAProvider()
    {
        var settlement = Settlement();
        var store = NewStore(settlement, receipt: null);
        var providers = new Mock<IBlockchainProviderFactory>(MockBehavior.Strict);

        var result = await new NodeFeeSettlementAtomicGroupReconciler(store.Object, providers.Object)
            .ReconcileDueAsync(Request());

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Be(new NodeFeeSettlementAtomicGroupReconciliationReport(1, 1, 0, 0, 1, 0));
        store.Verify(s => s.TryDeferToReconciliationAsync(
            It.IsAny<NodeFeeSettlementRecoveryLease>(),
            It.Is<string>(reason => reason.Contains("receipt", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
        providers.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReconcileDueAsync_OrdinaryPreparedRowWithoutReceipt_IsNeverMutatedObservedOrDeferred()
    {
        var settlement = Settlement();
        settlement.State = NodeFeeSettlement.StateKind.Prepared;
        settlement.PrimaryEffectState = NodeFeeSettlement.EffectStateKind.NotStarted;
        settlement.FeeEffectState = NodeFeeSettlement.EffectStateKind.NotStarted;
        settlement.PrimaryTransactionHash = null;
        settlement.FeeTransactionHash = null;
        var store = new Mock<INodeFeeSettlementStore>(MockBehavior.Strict);
        store.Setup(s => s.ListRecoverableAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IReadOnlyList<NodeFeeSettlement>>.Success(new[] { settlement }));
        store.Setup(s => s.TryClaimAcceptedAtomicGroupRecoveryAsync(
                settlement,
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<NodeFeeSettlement?>.Success(null));
        var providers = new Mock<IBlockchainProviderFactory>(MockBehavior.Strict);

        var result = await new NodeFeeSettlementAtomicGroupReconciler(store.Object, providers.Object)
            .ReconcileDueAsync(Request());

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Be(new NodeFeeSettlementAtomicGroupReconciliationReport(1, 0, 0, 0, 0, 1));
        settlement.State.Should().Be(NodeFeeSettlement.StateKind.Prepared);
        settlement.PrimaryEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.NotStarted);
        settlement.FeeEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.NotStarted);
        store.Verify(s => s.TryClaimRecoveryAsync(
            It.IsAny<NodeFeeSettlement>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(s => s.GetAcceptedAtomicGroupAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(s => s.TryDeferToReconciliationAsync(
            It.IsAny<NodeFeeSettlementRecoveryLease>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        providers.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReconcileDueAsync_InvalidReceipt_DefersWithoutResolvingAProvider()
    {
        var settlement = Settlement();
        var receipt = ReceiptFor(settlement);
        receipt.GroupIdentity = new string('b', 64);
        var store = NewStore(settlement, receipt);
        var providers = new Mock<IBlockchainProviderFactory>(MockBehavior.Strict);

        var result = await new NodeFeeSettlementAtomicGroupReconciler(store.Object, providers.Object)
            .ReconcileDueAsync(Request());

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Be(new NodeFeeSettlementAtomicGroupReconciliationReport(1, 1, 0, 0, 1, 0));
        store.Verify(s => s.TryDeferToReconciliationAsync(
            It.IsAny<NodeFeeSettlementRecoveryLease>(),
            It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
        providers.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileDueAsync_AbsentCapabilityOrMisbinding_RecordsUnavailableWithoutObservation(bool misbound)
    {
        var settlement = Settlement();
        var receipt = ReceiptFor(settlement);
        var store = NewStore(settlement, receipt);
        var provider = new Mock<IBlockchainProvider>(MockBehavior.Loose);
        provider.SetupGet(p => p.ChainType).Returns(misbound ? "Solana" : settlement.Chain);
        provider.SetupGet(p => p.ActiveNetwork).Returns(ChainNetwork.Mainnet);
        IAtomicTransferGroupObservationModule? missingCapability = null;
        provider.Setup(p => p.TryGetModule<IAtomicTransferGroupObservationModule>(out missingCapability)).Returns(false);
        var providers = new Mock<IBlockchainProviderFactory>(MockBehavior.Strict);
        providers.Setup(p => p.GetProvider(settlement.Chain, ChainNetwork.Mainnet)).Returns(provider.Object);
        NodeFeeSettlementEffectReconciliation? reconciliation = null;
        store.Setup(s => s.TryRecordNonTerminalReconciliationAsync(
                It.IsAny<NodeFeeSettlementRecoveryLease>(),
                It.IsAny<NodeFeeSettlementEffectReconciliation>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .Callback((NodeFeeSettlementRecoveryLease lease, NodeFeeSettlementEffectReconciliation value,
                string reason, DateTimeOffset nextAttemptAt, DateTimeOffset now, CancellationToken token) => reconciliation = value)
            .ReturnsAsync(AZOAResult<bool>.Success(true));

        var result = await new NodeFeeSettlementAtomicGroupReconciler(store.Object, providers.Object)
            .ReconcileDueAsync(Request());

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Be(new NodeFeeSettlementAtomicGroupReconciliationReport(1, 1, 0, 1, 0, 0));
        reconciliation.Should().Be(new NodeFeeSettlementEffectReconciliation(
            NodeFeeSettlement.EffectStateKind.Unknown, null,
            NodeFeeSettlement.EffectStateKind.Unknown, null));
        store.Verify(s => s.TrySettlePairedAsync(
            It.IsAny<NodeFeeSettlementRecoveryLease>(),
            It.IsAny<NodeFeeSettlementTerminalization>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileDueAsync_ClaimContention_DoesNotReadReceiptResolveProviderOrRetry()
    {
        var settlement = Settlement();
        var store = new Mock<INodeFeeSettlementStore>(MockBehavior.Strict);
        store.Setup(s => s.ListRecoverableAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IReadOnlyList<NodeFeeSettlement>>.Success(new[] { settlement }));
        store.Setup(s => s.TryClaimAcceptedAtomicGroupRecoveryAsync(
                settlement,
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<NodeFeeSettlement?>.Success(null));
        var providers = new Mock<IBlockchainProviderFactory>(MockBehavior.Strict);

        var result = await new NodeFeeSettlementAtomicGroupReconciler(store.Object, providers.Object)
            .ReconcileDueAsync(Request());

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Be(new NodeFeeSettlementAtomicGroupReconciliationReport(1, 0, 0, 0, 0, 1));
        store.Verify(s => s.TryClaimAcceptedAtomicGroupRecoveryAsync(
            settlement, It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        providers.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReconcileDueAsync_TerminalizationContention_DoesNotRetryObservationOrTerminalization()
    {
        var settlement = Settlement();
        var receipt = ReceiptFor(settlement);
        var store = NewStore(settlement, receipt);
        var observer = NewObserver(ConfirmedObservation(receipt, confirmedRound: 77));
        var providers = NewProviders(observer.Object);
        store.Setup(s => s.TrySettlePairedAsync(
                It.IsAny<NodeFeeSettlementRecoveryLease>(),
                It.IsAny<NodeFeeSettlementTerminalization>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Success(false));

        var result = await new NodeFeeSettlementAtomicGroupReconciler(store.Object, providers.Object)
            .ReconcileDueAsync(Request());

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Be(new NodeFeeSettlementAtomicGroupReconciliationReport(1, 1, 0, 0, 0, 1));
        observer.Verify(o => o.ObserveAtomicTransferGroupAsync(
            It.IsAny<AtomicTransferGroupObservationEvidence>(), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.TrySettlePairedAsync(
            It.IsAny<NodeFeeSettlementRecoveryLease>(),
            It.IsAny<NodeFeeSettlementTerminalization>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.TryRecordNonTerminalReconciliationAsync(
            It.IsAny<NodeFeeSettlementRecoveryLease>(),
            It.IsAny<NodeFeeSettlementEffectReconciliation>(),
            It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static NodeFeeSettlementRecoveryRequest Request() => new(
        DateTimeOffset.Parse("2026-07-18T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        BatchSize: 10,
        LeaseDuration: TimeSpan.FromMinutes(1),
        RetryDelay: TimeSpan.FromMinutes(5));

    private static NodeFeeSettlement Settlement(string? parentHash = null) => new()
    {
        Id = "settlement-001",
        ParentIdempotencyKeyHash = parentHash ?? new string('c', 64),
        Operation = "Transfer",
        Chain = "Algorand",
        Network = ChainNetwork.Mainnet.ToString(),
        AssetId = "42",
        GrossAmount = "100",
        FeeAmount = "20",
        NetAmount = "80",
        FeeScheduleVersion = 3,
        TreasuryAddress = "TREASURY",
        TreasuryDestinationVersion = 5,
        ExpectedAtomicGroupIdentity = new string('a', 64),
        State = NodeFeeSettlement.StateKind.AwaitingReconciliation,
        PrimaryEffectState = NodeFeeSettlement.EffectStateKind.Submitted,
        FeeEffectState = NodeFeeSettlement.EffectStateKind.Submitted,
        PrimaryTransactionHash = "primary-tx-001",
        FeeTransactionHash = "treasury-tx-001",
        StateVersion = 3,
    };

    private static NodeFeeAtomicGroup ReceiptFor(NodeFeeSettlement settlement) => new()
    {
        Id = NodeFeeAtomicGroup.RecordIdFor(settlement.Id),
        SettlementId = settlement.Id,
        GroupIdentity = settlement.ExpectedAtomicGroupIdentity!,
        ChainGroupId = "chain-group-001",
        SourceAddress = "SOURCE",
        PrimaryRecipientAddress = "RECIPIENT",
        PrimaryTransactionId = settlement.PrimaryTransactionHash!,
        TreasuryTransactionId = settlement.FeeTransactionHash!,
        State = NodeFeeAtomicGroup.StateKind.Submitted,
    };

    private static AtomicTransferGroupObservation ConfirmedObservation(NodeFeeAtomicGroup receipt, long confirmedRound) => new(
        AtomicTransferGroupObservationVerdict.Confirmed,
        new AtomicTransferLegObservation(receipt.PrimaryTransactionId, AtomicTransferLegObservationVerdict.Confirmed, confirmedRound),
        new AtomicTransferLegObservation(receipt.TreasuryTransactionId, AtomicTransferLegObservationVerdict.Confirmed, confirmedRound));

    private static AtomicTransferGroupObservation Observation(
        NodeFeeAtomicGroup receipt,
        AtomicTransferGroupObservationVerdict verdict,
        AtomicTransferLegObservationVerdict legVerdict) => new(
        verdict,
        new AtomicTransferLegObservation(receipt.PrimaryTransactionId, legVerdict, null),
        new AtomicTransferLegObservation(receipt.TreasuryTransactionId, legVerdict, null));

    private static Mock<IAtomicTransferGroupObservationModule> NewObserver(AtomicTransferGroupObservation observation)
    {
        var observer = new Mock<IAtomicTransferGroupObservationModule>(MockBehavior.Strict);
        observer.Setup(o => o.ObserveAtomicTransferGroupAsync(
                It.IsAny<AtomicTransferGroupObservationEvidence>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<AtomicTransferGroupObservation>.Success(observation));
        return observer;
    }

    private static Mock<IBlockchainProviderFactory> NewProviders(IAtomicTransferGroupObservationModule observer)
    {
        var provider = new Mock<IBlockchainProvider>(MockBehavior.Strict);
        provider.SetupGet(p => p.ChainType).Returns("Algorand");
        provider.SetupGet(p => p.ActiveNetwork).Returns(ChainNetwork.Mainnet);
        IAtomicTransferGroupObservationModule? module = observer;
        provider.Setup(p => p.TryGetModule<IAtomicTransferGroupObservationModule>(out module)).Returns(true);

        var providers = new Mock<IBlockchainProviderFactory>(MockBehavior.Strict);
        providers.Setup(p => p.GetProvider("Algorand", ChainNetwork.Mainnet)).Returns(provider.Object);
        return providers;
    }

    private static Mock<INodeFeeSettlementStore> NewStore(
        NodeFeeSettlement settlement,
        NodeFeeAtomicGroup? receipt)
    {
        var claimed = new NodeFeeSettlement
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
            ExpectedAtomicGroupIdentity = settlement.ExpectedAtomicGroupIdentity,
            State = settlement.State,
            PrimaryEffectState = settlement.PrimaryEffectState,
            FeeEffectState = settlement.FeeEffectState,
            PrimaryTransactionHash = settlement.PrimaryTransactionHash,
            FeeTransactionHash = settlement.FeeTransactionHash,
            StateVersion = settlement.StateVersion + 1,
            LeaseToken = "reconciliation-lease",
            LeaseExpiresAt = Request().Now.AddMinutes(1),
        };
        var store = new Mock<INodeFeeSettlementStore>(MockBehavior.Loose);
        store.Setup(s => s.ListRecoverableAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IReadOnlyList<NodeFeeSettlement>>.Success(new[] { settlement }));
        store.Setup(s => s.TryClaimAcceptedAtomicGroupRecoveryAsync(
                It.IsAny<NodeFeeSettlement>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<NodeFeeSettlement?>.Success(claimed));
        store.Setup(s => s.GetAcceptedAtomicGroupAsync(settlement.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<NodeFeeAtomicGroup?>.Success(receipt));
        store.Setup(s => s.TryDeferToReconciliationAsync(
                It.IsAny<NodeFeeSettlementRecoveryLease>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Success(true));
        store.Setup(s => s.TryRecordNonTerminalReconciliationAsync(
                It.IsAny<NodeFeeSettlementRecoveryLease>(),
                It.IsAny<NodeFeeSettlementEffectReconciliation>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Success(true));
        return store;
    }
}
