// SPDX-License-Identifier: UNLICENSED

using System.Text.Json;
using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Managers;

public sealed class AllocationReceiptManagerTests
{
    [Fact]
    public async Task GetAsync_ProjectsOnlySafeCallerReceipt()
    {
        var harness = new ReceiptHarness();
        var request = harness.Request();
        var operation = harness.Operation(OperationStatus.Minted);
        var allocation = harness.Allocation(operation.Id);
        harness.Configure(operation, IdempotencyState.Completed, allocation);

        var result = await harness.Manager.GetAsync(request);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.State.Should().Be(AllocationReceiptState.Completed);
        result.Result.OperationId.Should().Be(operation.Id);
        result.Result.AvatarId.Should().Be(allocation.AvatarId);
        result.Result.WalletAddress.Should().Be(allocation.WalletAddress);
        result.Result.TransactionReference.Should().Be("chain-tx-42");

        var json = JsonSerializer.Serialize(result.Result);
        json.Should().NotContain(request.ClientIdempotencyKey);
        json.Should().NotContain(harness.ApiKeyId.ToString());
        json.Should().NotContain("Parameters");
        json.Should().NotContain("Initiator");
    }

    [Fact]
    public async Task GetAsync_ForeignOperationAndAbsentLedgerShareTheSameNotFoundResponse()
    {
        var harness = new ReceiptHarness();
        var request = harness.Request();
        var foreign = harness.Operation(OperationStatus.Minted);
        foreign.InitiatorApiKeyId = Guid.NewGuid();
        harness.Configure(foreign, IdempotencyState.Completed, harness.Allocation(foreign.Id));

        var foreignResult = await harness.Manager.GetAsync(request);

        harness.IdempotencyStore
            .Setup(store => store.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdempotencyRecord?)null);
        var absentLedgerResult = await harness.Manager.GetAsync(request);

        foreignResult.IsError.Should().BeTrue();
        absentLedgerResult.IsError.Should().BeTrue();
        foreignResult.Code.Should().Be(AzoaErrorCodes.NotFound);
        absentLedgerResult.Code.Should().Be(AzoaErrorCodes.NotFound);
        foreignResult.Message.Should().Be(absentLedgerResult.Message);
    }

    [Fact]
    public async Task GetAsync_LedgerOnlyInProgressReceiptRemainsReadable()
    {
        var harness = new ReceiptHarness();
        var allocation = harness.Allocation(Guid.NewGuid());
        harness.ConfigureLedgerOnly(IdempotencyState.InProgress, allocation);

        var result = await harness.Manager.GetAsync(harness.Request());

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.State.Should().Be(AllocationReceiptState.AwaitingReconciliation);
        result.Result.OperationId.Should().BeNull();
        result.Result.RequiresReconciliation.Should().BeTrue();
    }

    [Fact]
    public async Task ReconcileAsync_LedgerOnlyReceiptDoesNotInvokeObservation()
    {
        var harness = new ReceiptHarness();
        harness.ConfigureLedgerOnly(IdempotencyState.InProgress, harness.Allocation(Guid.NewGuid()));

        var result = await harness.Manager.ReconcileAsync(harness.Request());

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.State.Should().Be(AllocationReceiptState.AwaitingReconciliation);
        harness.Reconciliation.Verify(
            service => service.ReconcileOperationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetAsync_DependencyFailureReturnsGenericUnavailableResult()
    {
        var harness = new ReceiptHarness();
        harness.IdempotencyStore
            .Setup(store => store.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("storage unavailable"));

        var result = await harness.Manager.GetAsync(harness.Request());

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(AzoaErrorCodes.DependencyUnavailable);
        result.Message.Should().NotContain("storage unavailable");
    }

    [Fact]
    public async Task ReconcileAsync_AwaitingReceiptObservesOnceAndReturnsRefreshedTerminalState()
    {
        var harness = new ReceiptHarness();
        var request = harness.Request();
        var operation = harness.Operation(OperationStatus.PendingConfirmation);
        var allocation = harness.Allocation(operation.Id);
        var ledger = harness.Configure(operation, IdempotencyState.InProgress, allocation);
        harness.Reconciliation
            .Setup(service => service.ReconcileOperationAsync(operation.Id, It.IsAny<CancellationToken>()))
            .Callback<Guid, CancellationToken>((_, _) =>
            {
                operation.Status = OperationStatus.Completed;
                ledger.State = IdempotencyState.Completed;
                ledger.UpdatedAt = DateTime.UtcNow;
            })
            .ReturnsAsync(ReconciliationReport.Empty with { Scanned = 1, Advanced = 1 });

        var result = await harness.Manager.ReconcileAsync(request);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.State.Should().Be(AllocationReceiptState.Completed);
        result.Result.RequiresReconciliation.Should().BeFalse();
        harness.Reconciliation.Verify(
            service => service.ReconcileOperationAsync(operation.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private sealed class ReceiptHarness
    {
        internal Guid ApiKeyId { get; } = Guid.NewGuid();
        private Guid AvatarId { get; } = Guid.NewGuid();
        internal Mock<IBlockchainOperationStore> OperationStore { get; } = new();
        internal Mock<IReconciliationService> Reconciliation { get; } = new();
        internal Mock<IIdempotencyStore> IdempotencyStore { get; } = new();

        internal AllocationReceiptManager Manager { get; }

        internal ReceiptHarness()
        {
            Manager = new AllocationReceiptManager(
                IdempotencyStore.Object,
                OperationStore.Object,
                Reconciliation.Object);
        }

        internal AllocationReceiptRequest Request() => new()
        {
            ApiKeyId = ApiKeyId,
            CallerAvatarId = AvatarId,
            ClientIdempotencyKey = "payment-intent-42",
        };

        internal BlockchainOperation Operation(string status)
        {
            var correlation = AllocationIdempotency.CreateFromClientKey(
                ApiKeyId,
                "payment-intent-42").Correlation;
            return new BlockchainOperation
            {
                Id = Guid.NewGuid(),
                IdempotencyKey = correlation,
                InitiatorAvatarId = AvatarId,
                InitiatorApiKeyId = ApiKeyId,
                Status = status,
                Parameters = new Dictionary<string, string>
                {
                    ["TxHash"] = "chain-tx-42",
                },
            };
        }

        internal AllocationResult Allocation(Guid operationId) => new()
        {
            AvatarId = Guid.NewGuid(),
            WalletId = Guid.NewGuid(),
            WalletAddress = "wallet-address-42",
            WalletProvisioned = true,
            OperationId = operationId,
            GrossAmount = "100",
            NodeFeeAmount = "5",
            NetAmount = "95",
            NodeFeeScheduleVersion = 7,
        };

        internal IdempotencyRecord Configure(
            BlockchainOperation operation,
            IdempotencyState state,
            AllocationResult allocation)
        {
            OperationStore
                .Setup(store => store.GetByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = operation });
            return ConfigureLedger(state, allocation);
        }

        internal IdempotencyRecord ConfigureLedgerOnly(
            IdempotencyState state,
            AllocationResult allocation)
        {
            OperationStore
                .Setup(store => store.GetByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<IBlockchainOperation>.FailureWithCode(
                    "Operation not found.",
                    AzoaErrorCodes.NotFound));
            return ConfigureLedger(state, allocation);
        }

        private IdempotencyRecord ConfigureLedger(
            IdempotencyState state,
            AllocationResult allocation)
        {
            var ledger = new IdempotencyRecord
            {
                OperationType = "fiat_allocation",
                State = state,
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                UpdatedAt = DateTime.UtcNow,
                ResultPayload = IdempotencyReplay.SerializeForReplay(allocation),
            };
            IdempotencyStore
                .Setup(store => store.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ledger);
            return ledger;
        }
    }
}
