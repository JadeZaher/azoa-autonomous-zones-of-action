// SPDX-License-Identifier: UNLICENSED
// W5.1 — every collaborator the exchange takes, pre-set to the happy path, plus faithful
// in-memory stores so the "moved exactly once" and "nothing stranded" assertions are real
// rather than mock-scripted.

using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Reserve;
using AZOA.WebAPI.Tests.TestSupport;
using Moq;

namespace AZOA.WebAPI.Tests.Exchange;

internal sealed class ExchangeTestHarness
{
    public const string Chain = "Algorand";
    public const string PlatformAsset = "1001";
    public const string ProjectAsset = "2002";
    public const string Partition = "investor";
    public const string ProjectReference = "project-alpha";
    public const string ReserveAddress = "RESERVEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    public const string TreasuryAddress = "TREASURYAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    public const string SponsorAddress = "SPONSORAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    public Guid ApiKeyId { get; } = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public Guid TenantId { get; } = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public Guid SponsorId { get; } = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    public Guid WalletId { get; } = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    public Mock<IValueAccessService> ValueAccess { get; } = new();
    public Mock<IPlatformReserve> Reserve { get; } = new();
    public Mock<IProjectTreasury> Treasury { get; } = new();
    public Mock<IPlatformHold> Holds { get; } = new();
    public Mock<IPlatformTokenGenesis> PlatformToken { get; } = new();
    public Mock<IWalletStore> Wallets { get; } = new();
    public Mock<ICustodialRecipientPreparer> Preparer { get; } = new();
    public Mock<IBlockchainProviderFactory> Providers { get; } = new();

    public FakeIdempotencyStore Idempotency { get; } = new();
    public RecordingExchangeGroupSubmitter Submitter { get; } = new();
    public RecordingDeclaredRateWriter DeclaredRates { get; } = new();
    public InMemoryOperationStore Operations { get; } = new();
    public ExchangeOptions Options { get; } = new();

    /// <summary>Every reserve draw the exchange asked for, in order, as (kind, amount, key).</summary>
    public List<(ReserveDrawKind Kind, BigInteger Amount, string OperationKey)> Draws { get; } = new();

    /// <summary>Every reserve draw in full, so a test can read the authority the seam stated (OI-20).</summary>
    public List<ReserveDrawRequest> DrawRequests { get; } = new();

    /// <summary>Every treasury movement the exchange asked for, in order.</summary>
    public List<(ProjectTreasuryMovementKind Kind, BigInteger Amount, string OperationKey)> Movements { get; } = new();

    /// <summary>Every hold the exchange opened or refunded, in order, as (verb, holdId).</summary>
    public List<(string Verb, string HoldId)> HoldCalls { get; } = new();

    /// <summary>Every hold-open in full, so a test can read the acting tenant the seam handed on (OI-21).</summary>
    public List<HoldOpenRequest> HoldOpens { get; } = new();

    public ExchangeTestHarness()
    {
        ValueAccess
            .Setup(access => access.RequireValueAccessAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Success(true));

        PlatformToken
            .Setup(genesis => genesis.GetStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<PlatformTokenState>.Success(new PlatformTokenState(
                new PlatformTokenDefinition(
                    Chain, "Devnet", PlatformAsset, "Arda", "ARDA", 2,
                    new BigInteger(1_000_000), ReserveAddress, DateTimeOffset.UtcNow.AddDays(-10)),
                new PlatformTokenSupply(
                    new BigInteger(1_000_000), BigInteger.Zero, BigInteger.Zero, new BigInteger(1_000_000)))));

        Wallets
            .Setup(store => store.GetByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IEnumerable<IWallet>>.Success(new[] { Wallet() }));

        Reserve
            .Setup(reserve => reserve.GetAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ReserveAccount>.Success(
                new ReserveAccount(Chain, ReserveAddress, IsFeePayer: true)));

        Reserve
            .Setup(reserve => reserve.DrawAsync(
                It.IsAny<ReserveDrawRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReserveDrawRequest request, CancellationToken _) =>
            {
                Draws.Add((request.Kind, request.Amount, request.OperationKey));
                DrawRequests.Add(request);
                return AZOAResult<ReserveDrawOutcome>.Success(new ReserveDrawOutcome(
                    request.AssetId, request.Partition, request.Kind, request.Amount,
                    ReservePartitionCounters.Declaring(new BigInteger(1_000_000)), 1, Replayed: false));
            });

        Treasury
            .Setup(treasury => treasury.ResolveAsync(
                It.IsAny<ProjectTreasuryReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProjectTreasuryReference reference, CancellationToken _) =>
                AZOAResult<ProjectTreasuryAccount>.Success(new ProjectTreasuryAccount(
                    reference.NormalizedChainType, reference.TenantId, reference.NormalizedProjectReference,
                    TreasuryAddress, "v1", ProjectTreasuryStatus.Open)));

        Treasury
            .Setup(treasury => treasury.RecordMovementAsync(
                It.IsAny<ProjectTreasuryMovementRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProjectTreasuryMovementRequest request, CancellationToken _) =>
            {
                Movements.Add((request.Kind, request.Amount, request.OperationKey));
                return AZOAResult<ProjectTreasuryMovementOutcome>.Success(new ProjectTreasuryMovementOutcome(
                    request.AssetId, request.Kind, request.Amount,
                    ProjectTreasuryAssetCounters.Empty, 1, Replayed: false));
            });

        Holds
            .Setup(holds => holds.OpenAsync(It.IsAny<HoldOpenRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HoldOpenRequest request, CancellationToken _) =>
            {
                HoldCalls.Add(("open", request.Reference.NormalizedHoldId));
                HoldOpens.Add(request);
                return AZOAResult<HoldOutcome>.Success(new HoldOutcome(
                    HoldRow(request), NextHold: null, TransactionId: null, Replayed: false));
            });

        Holds
            .Setup(holds => holds.RefundAsync(
                It.IsAny<HoldSettlementRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HoldSettlementRequest request, CancellationToken _) =>
            {
                HoldCalls.Add(("refund", request.Reference.NormalizedHoldId));
                return AZOAResult<HoldOutcome>.Success(new HoldOutcome(
                    new HoldLedger { HoldId = request.Reference.NormalizedHoldId },
                    NextHold: null, TransactionId: null, Replayed: false));
            });

        Preparer
            .Setup(preparer => preparer.PrepareAsync(
                It.IsAny<CustodialRecipientPreparation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<CustodialRecipientReadiness>.Success(
                new CustodialRecipientReadiness(
                    AlreadyPrepared: true, TransactionId: null, PendingConfirmation: false,
                    FundedBaseUnits: "0")));

        Providers.SetupGet(factory => factory.DefaultNetwork).Returns(ChainNetwork.Devnet);
    }

    public ReserveExchangeManager Build(
        IExchangeRatePolicy? ratePolicy = null,
        IExchangeGroupSubmitter? submitter = null)
        => new(
            ValueAccess.Object,
            Idempotency,
            Reserve.Object,
            Treasury.Object,
            Holds.Object,
            PlatformToken.Object,
            Wallets.Object,
            Preparer.Object,
            submitter ?? Submitter,
            ratePolicy ?? new TreasuryRateExchangePolicy(Options),
            DeclaredRates,
            Operations,
            Providers.Object,
            Options);

    public ExchangeCallerContext Caller(string idempotencyKey = "sponsorship-1") => new()
    {
        ApiKeyId = ApiKeyId,
        CallerUserId = TenantId,
        ActingTenantId = TenantId,
        TargetUserId = SponsorId,
        ClientIdempotencyKey = idempotencyKey,
    };

    public ExchangeRequest Request(
        ExchangeDirection direction = ExchangeDirection.Buy,
        DateTimeOffset? releaseNotBefore = null,
        bool previewOnly = false) => new()
    {
        ChainType = Chain,
        Direction = direction,
        ProjectReference = ProjectReference,
        ProjectAssetId = ProjectAsset,
        PlatformAmount = "500",
        ProjectUnitAmount = "250",
        ReservePartition = Partition,
        ReleaseNotBefore = releaseNotBefore,
        Purpose = "sponsorship",
        PreviewOnly = previewOnly,
        DeclaredRate = new DeclaredExchangeRate
        {
            CurrencyCode = "USD",
            MinorUnitsPerBaseUnit = "0.02",
            DeclaredAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Source = "ardanova:treasury-rate",
            Version = "3",
        },
    };

    /// <summary>Makes the reserve refuse the next draw the way an oversubscribed partition would.</summary>
    public void OversubscribeThePartition()
        => Reserve
            .Setup(reserve => reserve.DrawAsync(
                It.Is<ReserveDrawRequest>(request => request.Kind == ReserveDrawKind.Issue),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReserveDrawRequest request, CancellationToken _) =>
            {
                Draws.Add((request.Kind, request.Amount, request.OperationKey));
                DrawRequests.Add(request);
                return AZOAResult<ReserveDrawOutcome>.Failure(
                    $"Partition '{request.Partition}' would be oversubscribed: {request.Amount} base "
                    + "units requested, 0 available (declared 10, issued 10, held 0, returned 0).");
            });

    private IWallet Wallet() => new AZOA.WebAPI.Models.Wallet
    {
        Id = WalletId,
        UserId = SponsorId,
        ChainType = Chain,
        Address = SponsorAddress,
    };

    private static HoldLedger HoldRow(HoldOpenRequest request) => new()
    {
        HoldId = request.Reference.NormalizedHoldId,
        Chain = request.Reference.NormalizedChainType,
        AssetId = request.AssetId,
        Amount = request.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ReservePartition = request.ReservePartition ?? string.Empty,
        BeneficiaryUserId = request.BeneficiaryUserId.ToString("N"),
        ReleaseNotBefore = request.ReleaseNotBefore,
        State = HoldState.Held.ToString(),
    };
}

/// <summary>An exchange-group submitter that records what it was asked to settle.</summary>
internal sealed class RecordingExchangeGroupSubmitter : IExchangeGroupSubmitter
{
    private readonly bool _confirmed;
    private readonly string? _failure;

    public RecordingExchangeGroupSubmitter(bool confirmed = true, string? failure = null)
    {
        _confirmed = confirmed;
        _failure = failure;
    }

    public List<ExchangeGroupPlan> Submitted { get; } = new();

    public bool CanSubmit(string chainType) => true;

    public Task<AZOAResult<ExchangeGroupSubmission>> SubmitAsync(
        ExchangeGroupPlan plan, CancellationToken ct = default)
    {
        Submitted.Add(plan);
        if (_failure is not null)
        {
            return Task.FromResult(AZOAResult<ExchangeGroupSubmission>.FailureWithCode(
                _failure, ConsumerSafeErrorCodes.RailUnavailable));
        }

        var legReferences = plan.Legs
            .Select((_, index) => $"TX{index}{plan.GroupIdentity[..8].ToUpperInvariant()}")
            .ToArray();
        return Task.FromResult(AZOAResult<ExchangeGroupSubmission>.Success(
            new ExchangeGroupSubmission($"GROUP{plan.GroupIdentity[..8]}", legReferences, _confirmed)));
    }
}

/// <summary>A declared-rate writer that records every declaration it was handed.</summary>
internal sealed class RecordingDeclaredRateWriter : IExchangeDeclaredRateWriter
{
    public List<TenantDeclaredRate> Recorded { get; } = new();

    public Task<AZOAResult<TenantDeclaredRate>> RecordAsync(
        TenantDeclaredRate rate, CancellationToken ct = default)
    {
        Recorded.Add(rate);
        return Task.FromResult(AZOAResult<TenantDeclaredRate>.Success(rate));
    }
}

/// <summary>An operation store backed by a dictionary, so receipt reads exercise real lookups.</summary>
internal sealed class InMemoryOperationStore : IBlockchainOperationStore
{
    private readonly Dictionary<Guid, IBlockchainOperation> _byId = new();

    public IReadOnlyCollection<IBlockchainOperation> All => _byId.Values;

    /// <summary>
    /// Makes lookups answer "unreadable" rather than "absent" — a transient store failure, not a
    /// row that is not there. The two are different answers and the receipt seams must not
    /// conflate them: absence is describable from the ledger alone, unreadability is not.
    /// </summary>
    public bool ReadsFail { get; set; }

    public Task<AZOAResult<IBlockchainOperation>> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_byId.TryGetValue(id, out var found)
            ? AZOAResult<IBlockchainOperation>.Success(found)
            : AZOAResult<IBlockchainOperation>.FailureWithCode("Operation not found.", AzoaErrorCodes.NotFound));

    public Task<AZOAResult<IBlockchainOperation>> GetByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default)
    {
        if (ReadsFail)
        {
            return Task.FromResult(AZOAResult<IBlockchainOperation>.FailureWithCode(
                "The operation store is unavailable.", AzoaErrorCodes.DependencyUnavailable));
        }

        var found = _byId.Values.FirstOrDefault(operation =>
            string.Equals(operation.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
        return Task.FromResult(found is null
            ? AZOAResult<IBlockchainOperation>.FailureWithCode("Operation not found.", AzoaErrorCodes.NotFound)
            : AZOAResult<IBlockchainOperation>.Success(found));
    }

    public Task<AZOAResult<IEnumerable<IBlockchainOperation>>> GetByUserAsync(
        Guid userId, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<IEnumerable<IBlockchainOperation>>.Success(
            _byId.Values.Where(operation => operation.UserId == userId).ToList()));

    public Task<AZOAResult<IReadOnlyList<IBlockchainOperation>>> QueryAsync(
        BlockchainOperationQuery query, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<IReadOnlyList<IBlockchainOperation>>.Success(
            _byId.Values.Where(operation => operation.UserId == query.UserId).ToList()));

    public Task<AZOAResult<IBlockchainOperation>> UpsertAsync(
        IBlockchainOperation operation, CancellationToken ct = default)
    {
        _byId[operation.Id] = operation;
        return Task.FromResult(AZOAResult<IBlockchainOperation>.Success(operation));
    }

    public Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<bool>.Success(_byId.Remove(id)));
}
