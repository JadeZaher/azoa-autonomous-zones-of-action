// SPDX-License-Identifier: UNLICENSED
//
// OI-33, both halves, asserted at the counters.
//
// ReserveExchangeManager stops short of two things when the rail merely ACCEPTS a group, and
// leaves them to chain truth: it does not credit the project, and it does not unwind. Until this
// wave nothing ever finished either — a BUY the submitter reported Accepted moved the sponsor's
// units on chain and NEVER credited the project on the node's own ledger, and a group that
// dropped after acceptance left the partition permanently drawn with no sweeper.
//
// Every assertion below reads what the project's ledger and the partition's counters actually
// hold, never that a collaborator was called.

using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Exchange;

public sealed class ReconciledExchangeTests
{
    private const string Chain = "Algorand";
    private const string PlatformAsset = "1001";
    private const string ProjectAsset = "2002";
    private const string PartitionName = "investor";
    private const string ProjectReference = "project-alpha";
    private const string LedgerKey = "exchange:apikey:client-1";
    private const string ReserveAddress = "RESERVEADDRESS";

    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    // ── The credit a buy withheld until confirmation actually lands ───────────

    [Fact]
    public async Task A_confirmed_buy_whose_credit_was_outstanding_is_credited_exactly_once()
    {
        var world = new ExchangeWorld(declaredPartition: 1000);
        var operation = world.SeedBuy(creditApplied: false, platformAmount: 700, projectUnits: 350);

        var settled = await world.Reconciler().SettleConfirmedAsync(operation);

        settled.Should().BeTrue();
        world.TreasuryCredits.Should().ContainSingle().Which.Should().Be(
            new BigInteger(700),
            "the sponsor's units moved on chain; without this the project was never credited on "
            + "the node's own ledger, and that ledger is the availability control a later sell "
            + "is paid out against");
        operation.Parameters[ReserveExchangeManager.ProjectCreditAppliedParameter].Should().Be(
            "true", "the flag two read surfaces consult has to move with the money");

        // A second sweep must not credit again.
        (await world.Reconciler().SettleConfirmedAsync(operation)).Should().BeTrue();
        world.TreasuryCredits.Should().ContainSingle("a re-run is a replay, not a second credit");
    }

    [Fact]
    public async Task A_buy_whose_credit_still_cannot_be_recorded_is_refused_advancement()
    {
        var world = new ExchangeWorld(declaredPartition: 1000);
        world.TreasuryRefusesMovements();
        var operation = world.SeedBuy(creditApplied: false, platformAmount: 700, projectUnits: 350);

        var settled = await world.Reconciler().SettleConfirmedAsync(operation);

        settled.Should().BeFalse(
            "advancing it would settle the replay anchor over a credit that never landed, and a "
            + "settled anchor is never revisited");
        operation.Parameters[ReserveExchangeManager.ProjectCreditAppliedParameter].Should().Be(
            "false", "the row still states the truth: the credit is outstanding");
    }

    [Fact]
    public async Task A_sell_owes_no_project_credit_and_is_settled_untouched()
    {
        var world = new ExchangeWorld(declaredPartition: 1000);
        var operation = world.SeedSell(platformAmount: 700, projectUnits: 350);

        (await world.Reconciler().SettleConfirmedAsync(operation)).Should().BeTrue();

        world.TreasuryCredits.Should().BeEmpty("only a buy credits a project treasury");
    }

    // ── An accepted-then-failed group is actually unwound ────────────────────

    [Fact]
    public async Task A_buy_that_failed_on_chain_gives_the_partition_back()
    {
        var world = new ExchangeWorld(declaredPartition: 1000);
        var operation = world.SeedBuy(
            creditApplied: false, platformAmount: 700, projectUnits: 350,
            commitmentKind: ReserveExchangeManager.CommitmentKinds.PartitionIssue);

        // The partition as the in-request Issue draw left it.
        world.Draw(ReserveDrawKind.Issue, 350);
        world.Partition().Available.Should().Be(new BigInteger(650));

        (await world.Reconciler().CompensateFailedAsync(operation)).Should().BeTrue();

        world.Partition().Available.Should().Be(
            new BigInteger(1000),
            "a group that died after acceptance left the earmark permanently spoken for; nothing "
            + "unwound it, and the next sponsor was refused for units nobody holds");
        world.TreasuryCredits.Should().BeEmpty("a buy spends no project treasury to put back");
    }

    [Fact]
    public async Task A_sell_that_failed_on_chain_puts_the_treasury_back_and_re_draws_the_partition()
    {
        var world = new ExchangeWorld(declaredPartition: 1000);
        var operation = world.SeedSell(
            platformAmount: 700, projectUnits: 350, treasurySpent: true);

        // The partition as the in-request Return draw left it: units came BACK on a sell.
        world.Draw(ReserveDrawKind.Issue, 350);
        world.Draw(ReserveDrawKind.Return, 350);
        world.Partition().Available.Should().Be(new BigInteger(1000));

        (await world.Reconciler().CompensateFailedAsync(operation)).Should().BeTrue();

        world.Partition().Available.Should().Be(
            new BigInteger(650), "the units the sell said came back never did");
        world.TreasuryCredits.Should().ContainSingle().Which.Should().Be(
            new BigInteger(700),
            "the project paid for units it never received; without this the treasury stays short");
    }

    [Fact]
    public async Task A_row_that_does_not_name_its_commitment_hands_nothing_back()
    {
        var world = new ExchangeWorld(declaredPartition: 1000);
        var operation = world.SeedBuy(
            creditApplied: false, platformAmount: 700, projectUnits: 350, commitmentKind: null);

        world.Draw(ReserveDrawKind.Issue, 350);

        (await world.Reconciler().CompensateFailedAsync(operation)).Should().BeFalse(
            "a guessed unwind hands the partition units nobody took — refusing and flagging is "
            + "the safe answer");

        world.Partition().Available.Should().Be(
            new BigInteger(650), "no counter moved on a guess");
    }

    // ── The world under test ─────────────────────────────────────────────────

    private sealed class ExchangeWorld
    {
        private readonly CountingReserve _reserve;
        private readonly Mock<IProjectTreasury> _treasury = new();
        private readonly Mock<IPlatformHold> _holds = new();
        private readonly Mock<IBlockchainOperationStore> _operations = new();

        private bool _treasuryRefuses;

        public ExchangeWorld(long declaredPartition)
        {
            _reserve = new CountingReserve(Chain, ProjectAsset, PartitionName, declaredPartition);

            _treasury
                .Setup(t => t.RecordMovementAsync(
                    It.IsAny<ProjectTreasuryMovementRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ProjectTreasuryMovementRequest request, CancellationToken _) =>
                {
                    if (_treasuryRefuses)
                        return AZOAResult<ProjectTreasuryMovementOutcome>.Failure("The ledger is unavailable.");

                    // The real ledger is idempotent on the operation key; a fake that was not
                    // would let a double-credit pass as a pass.
                    if (!AppliedKeys.Add(request.OperationKey))
                    {
                        return AZOAResult<ProjectTreasuryMovementOutcome>.Success(
                            new ProjectTreasuryMovementOutcome(
                                request.AssetId, request.Kind, request.Amount,
                                new ProjectTreasuryAssetCounters(
                                    request.Amount, BigInteger.Zero, BigInteger.Zero),
                                1, Replayed: true));
                    }

                    TreasuryCredits.Add(request.Amount);
                    return AZOAResult<ProjectTreasuryMovementOutcome>.Success(
                        new ProjectTreasuryMovementOutcome(
                            request.AssetId, request.Kind, request.Amount,
                            new ProjectTreasuryAssetCounters(
                                request.Amount, BigInteger.Zero, BigInteger.Zero),
                            1, Replayed: false));
                });

            _operations
                .Setup(s => s.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IBlockchainOperation op, CancellationToken _) =>
                    AZOAResult<IBlockchainOperation>.Success(op));
        }

        public List<BigInteger> TreasuryCredits { get; } = new();

        private HashSet<string> AppliedKeys { get; } = new(StringComparer.Ordinal);

        public void TreasuryRefusesMovements() => _treasuryRefuses = true;

        public ExchangeSettlementReconciler Reconciler() =>
            new(_treasury.Object, _reserve, _holds.Object, _operations.Object);

        public ReservePartitionCounters Partition() => _reserve.Counters();

        /// <summary>Moves the partition the way the in-request path moved it.</summary>
        public void Draw(ReserveDrawKind kind, long amount) =>
            _reserve.DrawAsync(new ReserveDrawRequest(
                    Chain, ProjectAsset, PartitionName, kind, new BigInteger(amount),
                    $"{LedgerKey}:{kind}", ReserveDrawAuthority.ForTenant(TenantId)))
                .GetAwaiter().GetResult();

        public BlockchainOperation SeedBuy(
            bool creditApplied,
            long platformAmount,
            long projectUnits,
            string? commitmentKind = ReserveExchangeManager.CommitmentKinds.PartitionIssue) =>
            Seed(ExchangeDirection.Buy, platformAmount, projectUnits, commitmentKind,
                creditApplied: creditApplied, treasurySpent: false);

        public BlockchainOperation SeedSell(
            long platformAmount, long projectUnits, bool treasurySpent = false) =>
            Seed(ExchangeDirection.Sell, platformAmount, projectUnits,
                ReserveExchangeManager.CommitmentKinds.PartitionReturn,
                creditApplied: null, treasurySpent: treasurySpent);

        /// <summary>An Exchange row exactly as <c>ReserveExchangeManager.BuildOperation</c> writes it.</summary>
        private static BlockchainOperation Seed(
            ExchangeDirection direction,
            long platformAmount,
            long projectUnits,
            string? commitmentKind,
            bool? creditApplied,
            bool treasurySpent)
        {
            var buying = direction == ExchangeDirection.Buy;

            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TxHash"] = "GROUPLEG1",
                ["ChainType"] = Chain,
                ["Amount"] = (buying ? platformAmount : projectUnits).ToString(),
                ["AmountOut"] = (buying ? projectUnits : platformAmount).ToString(),
                ["SourceTokenId"] = buying ? PlatformAsset : ProjectAsset,
                ["TargetTokenId"] = buying ? ProjectAsset : PlatformAsset,
                ["ExchangeDirection"] = direction.ToString(),
                ["ProjectReference"] = ProjectReference,
                ["ReserveAddress"] = ReserveAddress,
                ["ReservePartition"] = PartitionName,
                ["IdempotencyKey"] = LedgerKey,
                [IdempotencyParameterNames.ResultPayload] =
                    IdempotencyReplay.SerializeForReplay(new ExchangeReceipt
                    {
                        ReceiptReference = "correlation-1",
                        State = ExchangeReceiptState.Completed,
                    }),
                [ReserveExchangeManager.CommitmentTreasurySpentParameter] =
                    treasurySpent ? "true" : "false",
            };

            if (commitmentKind is not null)
                parameters[ReserveExchangeManager.CommitmentKindParameter] = commitmentKind;

            if (creditApplied is { } applied)
            {
                parameters[ReserveExchangeManager.ProjectCreditAppliedParameter] =
                    applied ? "true" : "false";
            }

            return new BlockchainOperation
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                ActingTenantId = TenantId,
                OperationType = ReserveExchangeManager.ChainOperationType,
                Status = OperationStatus.PendingConfirmation,
                Amount = RailAmount.OfChainAsset(new BigInteger(buying ? platformAmount : projectUnits)),
                CreatedDate = DateTime.UtcNow.AddMinutes(-30),
                Parameters = parameters,
            };
        }
    }

    /// <summary>
    /// A reserve backed by the REAL partition arithmetic and the real replay window, so an
    /// oversubscribed or duplicated draw is refused here for the reason it is refused in
    /// production.
    /// </summary>
    private sealed class CountingReserve : IPlatformReserve
    {
        private readonly string _chain;
        private readonly string _assetId;
        private readonly string _partition;
        private readonly HashSet<string> _appliedKeys = new(StringComparer.Ordinal);
        private ReservePartitionCounters _counters;

        public CountingReserve(string chain, string assetId, string partition, long declared)
        {
            _chain = chain;
            _assetId = assetId;
            _partition = ReservePartitionCodec.Normalize(partition);
            _counters = ReservePartitionCounters.Declaring(new BigInteger(declared));
        }

        public ReservePartitionCounters Counters() => _counters;

        public Task<AZOAResult<ReserveDrawOutcome>> DrawAsync(
            ReserveDrawRequest request, CancellationToken ct = default)
        {
            if (request.Authority is null)
            {
                return Task.FromResult(AZOAResult<ReserveDrawOutcome>.Failure(
                    "A draw arrived with no stated authority."));
            }

            var partition = ReservePartitionCodec.Normalize(request.Partition);
            if (partition != _partition)
            {
                return Task.FromResult(AZOAResult<ReserveDrawOutcome>.Failure(
                    $"Partition '{partition}' is not declared on asset '{_assetId}'."));
            }

            if (!_appliedKeys.Add(request.OperationKey))
            {
                return Task.FromResult(AZOAResult<ReserveDrawOutcome>.Success(
                    new ReserveDrawOutcome(
                        _assetId, partition, request.Kind, request.Amount, _counters, 1, Replayed: true)));
            }

            if (!_counters.TryApply(request.Kind, request.Amount, partition, out var next, out var error))
                return Task.FromResult(AZOAResult<ReserveDrawOutcome>.Failure(error));

            _counters = next;
            return Task.FromResult(AZOAResult<ReserveDrawOutcome>.Success(
                new ReserveDrawOutcome(
                    _assetId, partition, request.Kind, request.Amount, next, 1, Replayed: false)));
        }

        public Task<AZOAResult<ReserveAccount>> GetAccountAsync(
            string chainType, CancellationToken ct = default) =>
            Task.FromResult(AZOAResult<ReserveAccount>.Success(
                new ReserveAccount(_chain, ReserveAddress, IsFeePayer: true)));

        public Task<AZOAResult<PlatformReserveLedger>> GetLedgerAsync(
            string chainType, string assetId, CancellationToken ct = default) =>
            throw new NotSupportedException("The settlement reconciler reads no ledger row.");

        public Task<AZOAResult<string>> GetBalanceAsync(
            string chainType, string? assetId = null, CancellationToken ct = default) =>
            throw new NotSupportedException("The settlement reconciler reads no chain balance.");

        public Task<AZOAResult<T>> SignAsReserveAsync<T>(
            string chainType, SigningContext signingContext, Func<byte[], Task<T>> sign) =>
            throw new NotSupportedException("The settlement reconciler signs nothing.");

        public Task<AZOAResult<PlatformReserveLedger>> RegisterAssetAsync(
            ReserveAssetRegistration registration, CancellationToken ct = default) =>
            throw new NotSupportedException("The settlement reconciler registers no assets.");

        public Task<AZOAResult<PlatformReserveLedger>> DeclarePartitionsAsync(
            string chainType,
            string assetId,
            IReadOnlyDictionary<string, BigInteger> declaredByPartition,
            ReserveDrawAuthority authority,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Partitions are declared by the operator.");
    }
}
