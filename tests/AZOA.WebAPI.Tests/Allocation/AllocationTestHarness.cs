// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Tests.Allocation;

/// <summary>
/// Wiring for the W3.3 issuance tests: every collaborator the allocation seam takes,
/// pre-set to the happy path, plus a faithful in-memory idempotency store so the
/// "moved exactly once" assertions are real rather than mock-scripted.
/// </summary>
internal sealed class AllocationTestHarness
{
    public const string ChainType = "Algorand";
    public const string AssetId = "77712345";
    public const string Partition = "contributor";
    public const string ReserveAddress = "RESERVEADDRESSAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    public const string RecipientAddress = "RECIPIENTADDRESSAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    public const string TreasuryAddress = "TREASURYADDRESSAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    public static readonly Guid ApiKeyGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static string ApiKeyId => ApiKeyGuid.ToString();

    public Guid UserId { get; } = Guid.NewGuid();
    public Guid CallerUserId { get; } = Guid.NewGuid();
    public Guid WalletId { get; } = Guid.NewGuid();

    public Mock<IValueAccessService> ValueAccess { get; } = new();
    public Mock<IWalletManager> WalletManager { get; } = new();
    public Mock<IWalletStore> WalletStore { get; } = new();
    public Mock<IHolonStore> HolonStore { get; } = new();
    public Mock<INftManager> Nft { get; } = new();
    public Mock<IBlockchainOperationManager> BlockchainOps { get; } = new();
    public Mock<INodeFeeScheduleManager> NodeFees { get; } = new();
    public Mock<IPlatformReserve> Reserve { get; } = new();
    public Mock<ICustodialRecipientPreparer> Preparer { get; } = new();
    public Mock<INodeFeeSettlementManager> FeeSettlement { get; } = new();
    public Mock<INodeTreasuryManager> Treasury { get; } = new();
    public Mock<IBlockchainProviderFactory> Providers { get; } = new();
    public InMemoryIdempotencyStore Idempotency { get; } = new();

    /// <summary>The op the broadcast seam last received, or null when it was never reached.</summary>
    public IBlockchainOperation? BroadcastOp { get; private set; }

    /// <summary>Ordered names of the value-path steps that ran, for ordering assertions.</summary>
    public List<string> Steps { get; } = new();

    public AllocationTestHarness()
    {
        ValueAccess
            .Setup(access => access.RequireValueAccessAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true, Message = "Success" });

        WalletStore
            .Setup(store => store.GetByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new[] { Wallet() } });

        HolonStore
            .Setup(store => store.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IHolon holon, CancellationToken _) => new AZOAResult<IHolon> { Result = holon });

        QuoteFee(feeAmount: 0);

        Reserve
            .Setup(reserve => reserve.GetAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<ReserveAccount>
            {
                Result = new ReserveAccount(ChainType, ReserveAddress, IsFeePayer: true),
            });

        Reserve
            .Setup(reserve => reserve.DrawAsync(It.IsAny<ReserveDrawRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReserveDrawRequest request, CancellationToken _) =>
            {
                Steps.Add("draw");
                return new AZOAResult<ReserveDrawOutcome>
                {
                    Result = new ReserveDrawOutcome(
                        request.AssetId, request.Partition, request.Kind, request.Amount,
                        new ReservePartitionCounters(
                            new BigInteger(1_000_000), request.Amount, BigInteger.Zero, BigInteger.Zero),
                        Version: 2, Replayed: false),
                };
            });

        PreparerReturns(alreadyPrepared: true);
        BroadcastReturnsTransactionReference();

        Providers.SetupGet(providers => providers.DefaultNetwork).Returns(ChainNetwork.Devnet);

        Treasury
            .Setup(treasury => treasury.GetDestinationAsync(
                It.IsAny<string>(), It.IsAny<ChainNetwork>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<NodeTreasuryDestinationResponse>
            {
                Result = new NodeTreasuryDestinationResponse
                {
                    Chain = ChainType,
                    Network = ChainNetwork.Devnet,
                    Address = TreasuryAddress,
                    Version = 3,
                },
            });

        FeeSettlement
            .Setup(settlement => settlement.PrepareAsync(
                It.IsAny<NodeFeeSettlementDraft>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<NodeFeeSettlement> { Result = new NodeFeeSettlement() });
    }

    public IWallet Wallet() => Mock.Of<IWallet>(wallet =>
        wallet.Id == WalletId &&
        wallet.UserId == UserId &&
        wallet.ChainType == ChainType &&
        wallet.Address == RecipientAddress &&
        wallet.WalletType == WalletType.Platform);

    /// <summary>Fee schedule answer for every operation, so a kind change needs no re-setup.</summary>
    public void QuoteFee(ulong feeAmount, long scheduleVersion = 7)
        => NodeFees
            .Setup(fees => fees.QuoteAsync(
                It.IsAny<NodeFeeOperation>(), It.IsAny<ulong>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NodeFeeOperation operation, ulong gross, CancellationToken _) =>
                new AZOAResult<NodeFeeQuoteResponse>
                {
                    Result = new NodeFeeQuoteResponse
                    {
                        Operation = operation,
                        GrossAmount = gross.ToString(CultureInfo.InvariantCulture),
                        FeeAmount = feeAmount.ToString(CultureInfo.InvariantCulture),
                        NetAmount = (gross - feeAmount).ToString(CultureInfo.InvariantCulture),
                        ScheduleVersion = scheduleVersion,
                    },
                    Message = "Success",
                });

    public void PreparerReturns(bool alreadyPrepared, bool pendingConfirmation = false)
        => Preparer
            .Setup(preparer => preparer.PrepareAsync(
                It.IsAny<CustodialRecipientPreparation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Steps.Add("prepare");
                return AZOAResult<CustodialRecipientReadiness>.Success(
                    new CustodialRecipientReadiness(
                        alreadyPrepared,
                        alreadyPrepared ? null : "ACCEPTTX",
                        pendingConfirmation,
                        alreadyPrepared ? "0" : "202000"));
            });

    public void PreparerFails(string message)
        => Preparer
            .Setup(preparer => preparer.PrepareAsync(
                It.IsAny<CustodialRecipientPreparation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Steps.Add("prepare");
                return AZOAResult<CustodialRecipientReadiness>.Failure(message);
            });

    public void DrawFails(string message)
        => Reserve
            .Setup(reserve => reserve.DrawAsync(It.IsAny<ReserveDrawRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Steps.Add("draw");
                return AZOAResult<ReserveDrawOutcome>.Failure(message);
            });

    public void BroadcastReturnsTransactionReference(string reference = "SETTLEMENTREF")
        => BlockchainOps
            .Setup(ops => ops.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync((IBlockchainOperation op, AZOARequest? _) =>
            {
                Steps.Add("broadcast");
                BroadcastOp = op;
                op.Parameters["TxHash"] = reference;
                op.Status = OperationStatus.Transferred;
                return new AZOAResult<IBlockchainOperation> { Result = op };
            });

    public void BroadcastFails(string message)
        => BlockchainOps
            .Setup(ops => ops.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync((IBlockchainOperation op, AZOARequest? _) =>
            {
                Steps.Add("broadcast");
                BroadcastOp = op;
                return AZOAResult<IBlockchainOperation>.Failure(message, op);
            });

    /// <summary>Builds the manager. Pass false to model a node with no reserve wired.</summary>
    public AllocationManager Build(bool withReserve = true, bool withFeeSettlement = true)
        => new(
            ValueAccess.Object, WalletManager.Object, WalletStore.Object, HolonStore.Object,
            Nft.Object, BlockchainOps.Object, Idempotency, NodeFees.Object,
            nodeGovernance: null,
            users: null,
            reserve: withReserve ? Reserve.Object : null,
            recipientPreparer: withReserve ? Preparer.Object : null,
            feeSettlement: withFeeSettlement ? FeeSettlement.Object : null,
            treasury: withFeeSettlement ? Treasury.Object : null,
            providers: Providers.Object,
            logger: null);

    public static AllocationRequest IssueUnitsRequest(
        string amount = "1000",
        string? assetId = AssetId,
        string? partition = Partition,
        string? purpose = "milestone-2 contributor stake")
        => new()
        {
            Kind = AllocationKind.IssueUnits,
            ChainType = ChainType,
            Amount = amount,
            Name = "Riverbank cleanup",
            AssetId = assetId,
            Partition = partition,
            Purpose = purpose,
        };

    /// <summary>Faithful claim-once / replay-cached store; the same model the seam sees in production.</summary>
    internal sealed class InMemoryIdempotencyStore : IIdempotencyStore
    {
        private readonly Dictionary<string, IdempotencyRecord> _records = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, IdempotencyRecord> Records => _records;

        public Task<IdempotencyClaim> TryClaimAsync(string key, string operationType, CancellationToken ct)
        {
            if (_records.TryGetValue(key, out var existing))
                return Task.FromResult(new IdempotencyClaim(false, Clone(existing)));

            var record = new IdempotencyRecord
            {
                Key = key,
                OperationType = operationType,
                State = IdempotencyState.InProgress,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _records[key] = record;
            return Task.FromResult(new IdempotencyClaim(true, Clone(record)));
        }

        public Task CompleteAsync(string key, string resultPayload, CancellationToken ct)
        {
            if (_records.TryGetValue(key, out var record) && record.State == IdempotencyState.InProgress)
            {
                record.State = IdempotencyState.Completed;
                record.ResultPayload = resultPayload;
                record.UpdatedAt = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }

        public Task FailAsync(string key, string error, CancellationToken ct)
        {
            if (_records.TryGetValue(key, out var record) && record.State == IdempotencyState.InProgress)
            {
                record.State = IdempotencyState.Failed;
                record.Error = error;
                record.UpdatedAt = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }

        public Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct)
            => Task.FromResult(_records.TryGetValue(key, out var record) ? Clone(record) : null);

        private static IdempotencyRecord Clone(IdempotencyRecord record) => new()
        {
            Key = record.Key,
            OperationType = record.OperationType,
            State = record.State,
            ResultPayload = record.ResultPayload,
            Error = record.Error,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
        };
    }
}
