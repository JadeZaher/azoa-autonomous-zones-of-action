// SPDX-License-Identifier: UNLICENSED
// OI-44 / OI-60 - "you are 4th in line and the platform stands behind it" is a promise a member
// will plan around, so either the code keeps it or the node must not say it. These tests assert
// at the COUNTER: what the reserve partition actually holds, what it issued, and how many places
// the queue actually contains - never that a collaborator was called.

using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Reserve;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Exchange;

public sealed class BuyBackQueueTests
{
    private const string Chain = "Algorand";
    private const string PlatformAsset = "1001";
    private const string ProjectAsset = "2002";
    private const string BackstopPartition = "buyback-backstop";
    private const string ReserveAddress = "RESERVEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string TreasuryAddress = "TREASURYAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    // ── (c) a retry keeps its place, and takes no second slice of the budget ──

    [Fact]
    public async Task A_retry_of_the_same_sale_keeps_its_place_and_takes_no_second_set_aside()
    {
        var world = new BuyBackWorld(declaredBackstop: 500);
        world.RailRefusesToMove();

        var first = await world.Policy().EnsureSellAllowedAsync(world.Sell("sale-1", 500));
        var second = await world.Policy().EnsureSellAllowedAsync(world.Sell("sale-1", 500));

        first.Message.Should().Contain("number 1 in line");
        second.Message.Should().Contain(
            "number 1 in line",
            "a retry re-reads the place it already has - it does not go to the back of the queue");

        world.OpenPlaces().Should().Be(1, "one open sale is one place in line, however often it is retried");
        world.Partition().Held.Should().Be(
            new BigInteger(500),
            "the second attempt found the set-aside its own place was already carrying, so it did "
            + "not take a second one out of the same declared budget");
    }

    // ── (b) + (d) a filled sell actually spends the declared budget ───────────

    [Fact]
    public async Task A_filled_sell_moves_reserve_units_into_the_treasury_and_decrements_the_backstop()
    {
        var world = new BuyBackWorld(declaredBackstop: 1000);

        var allowed = await world.Policy().EnsureSellAllowedAsync(world.Sell("sale-1", 500));

        allowed.IsError.Should().BeFalse("the reserve covered the shortfall, so the treasury can pay");

        var partition = world.Partition();
        partition.Issued.Should().Be(new BigInteger(500), "the units left the backstop partition");
        partition.Held.Should().Be(BigInteger.Zero, "the set-aside was spent, not left hanging");
        partition.Available.Should().Be(
            new BigInteger(500),
            "a declared budget of 1000 that backed one 500-unit sell has 500 left, not 1000");

        world.TreasuryBalance.Should().Be(new BigInteger(500), "the units reached the project's account");
        world.TreasuryCredits.Should().ContainSingle().Which.Should().Be(new BigInteger(500));
        world.OpenPlaces().Should().Be(0, "a filled sell does not stay in the queue");
    }

    // ── (d) one declared budget cannot back two sellers at once ───────────────

    [Fact]
    public async Task Two_queued_sellers_cannot_both_be_backed_by_the_same_declared_budget()
    {
        var world = new BuyBackWorld(declaredBackstop: 500);
        world.RailRefusesToMove();

        var backed = await world.Policy().EnsureSellAllowedAsync(
            world.Sell("sale-1", 500, projectReference: "project-alpha"));
        var unbacked = await world.Policy().EnsureSellAllowedAsync(
            world.Sell("sale-2", 500, projectReference: "project-beta"));

        backed.Message.Should().Contain(
            "backed by the platform", "the first seller's promise was booked against the partition");

        unbacked.Message.Should().NotContain(
            "backed by the platform",
            "the whole declared budget is already promised to the first seller, and saying "
            + "otherwise is a promise the node cannot keep");
        unbacked.Message.Should().Contain("as the project's funds recover");
        unbacked.Message.Should().Contain("number 1 in line", "a second project has its own queue");

        world.Partition().Held.Should().Be(
            new BigInteger(500),
            "500 was declared and 500 is promised - a second promise would have made it 1000");
    }

    // ── (a) the place survives as its own answer, not as a bare refusal ───────

    [Fact]
    public async Task A_place_in_line_carries_its_own_code_and_no_rail_words()
    {
        var world = new BuyBackWorld(declaredBackstop: 0);

        var queued = await world.Policy().EnsureSellAllowedAsync(world.Sell("sale-1", 500));

        queued.IsError.Should().BeTrue("nothing moved, so this is not a success");
        queued.Code.Should().Be(
            BuyBackLiquidityPolicy.QueuedForFillCode,
            "a place in line is its own answer; under a generic funds code a consumer re-words it "
            + "away and the seller never learns they have a place at all");
        queued.Code.Should().NotBe(ConsumerSafeErrorCodes.InsufficientFunds);
        queued.Message.Should().Contain("number 1 in line");
        ConsumerSafeErrors.LeaksRailDetail(queued.Message).Should().BeFalse(
            "a sponsor who has never heard of a market reads this sentence");
    }

    // ── the promise dies with the place it backed ─────────────────────────────

    [Fact]
    public async Task An_expired_place_gives_its_set_aside_back_to_the_next_seller()
    {
        var world = new BuyBackWorld(declaredBackstop: 500);
        world.Options.QueueEntryTtl = TimeSpan.FromHours(1);
        world.RailRefusesToMove();

        await world.Policy().EnsureSellAllowedAsync(world.Sell("sale-1", 500));
        world.Partition().Held.Should().Be(new BigInteger(500));

        var later = Now.AddHours(3);
        var next = await world.Policy().EnsureSellAllowedAsync(
            world.Sell("sale-2", 500, now: later));

        next.Message.Should().Contain(
            "backed by the platform",
            "the abandoned place stopped holding the budget when it expired");
        world.Partition().Held.Should().Be(
            new BigInteger(500),
            "the expired set-aside came back and the new one took its place - it did not stack");
        world.OpenPlaces(later).Should().Be(1, "the expired place is retired, not counted");
    }

    // ── A movement the rail took and never confirmed is NOT a movement that landed ──
    //
    // The whole class of defect in one test: BuildSignSubmitAsync returns a SUCCESS result whose
    // Message carries PendingConfirmationMarker when the confirm poll times out. Reading only
    // IsError converted the hold to Issued, credited the project and completed the replay anchor
    // for units that may never have left the reserve — permanently, because a completed anchor is
    // never re-attempted. Every assertion below is a COUNTER, not a call.

    [Fact]
    public async Task A_broadcast_that_never_confirmed_neither_issues_the_set_aside_nor_credits_the_project()
    {
        var world = new BuyBackWorld(declaredBackstop: 1000);
        world.RailTakesItAndSaysNothing();

        var outcome = await world.Policy().EnsureSellAllowedAsync(world.Sell("sale-1", 500));

        outcome.IsError.Should().BeTrue(
            "the treasury cannot be shown to hold these units, so the sell is not admitted");

        var partition = world.Partition();
        partition.Issued.Should().Be(
            BigInteger.Zero,
            "nothing established that the units left the reserve, and Issued is a claim that they did");
        partition.Held.Should().Be(
            new BigInteger(500),
            "the set-aside stays exactly what it was: held against this seller's place");
        partition.Available.Should().Be(
            new BigInteger(500), "a held unit and an issued unit reduce availability identically");

        world.TreasuryCredits.Should().BeEmpty(
            "crediting a project for units that may never have arrived makes it spendably richer "
            + "on a settlement chain truth has not agreed to");
        world.OpenPlaces().Should().Be(1, "the seller keeps their place and their promise");
    }

    [Fact]
    public async Task A_broadcast_that_never_confirmed_resumes_its_saved_bytes_and_settles_exactly_once()
    {
        var world = new BuyBackWorld(declaredBackstop: 1000);
        world.RailTakesItAndSaysNothing();

        await world.Policy().EnsureSellAllowedAsync(world.Sell("sale-1", 500));
        await world.Policy().EnsureSellAllowedAsync(world.Sell("sale-1", 500));

        world.MoveClaim("sale-1")!.State.Should().Be(
            IdempotencyState.InProgress,
            "completing the anchor over an unknown outcome is what makes the damage permanent");
        world.PreparedTransfers.Should().Be(1, "a retry rebroadcasts the saved bytes; it never signs a second transfer");

        world.RailConfirmsWhatItTook();
        var settled = await world.Policy().EnsureSellAllowedAsync(world.Sell("sale-1", 500));

        settled.IsError.Should().BeFalse(settled.Message);
        world.MoveClaim("sale-1")!.State.Should().Be(IdempotencyState.Completed);
        world.PreparedTransfers.Should().Be(1);
        world.TreasuryCredits.Should().ContainSingle().Which.Should().Be(new BigInteger(500));
        world.Partition().Issued.Should().Be(new BigInteger(500));
        world.Partition().Held.Should().Be(BigInteger.Zero);
    }

    [Fact]
    public async Task A_set_aside_whose_movement_was_never_established_is_not_handed_back()
    {
        var world = new BuyBackWorld(declaredBackstop: 1000);
        world.RailTakesItAndSaysNothing();

        await world.Policy().EnsureSellAllowedAsync(world.Sell("sale-1", 500));

        var givenBack = await world.GiveBackSetAside("sale-1", 500);

        givenBack.IsError.Should().BeTrue(
            "the units may already be the project's, and handing the partition its availability "
            + "back would let the node promise the same units to a second seller");

        world.Partition().Held.Should().Be(
            new BigInteger(500), "the set-aside is frozen, not released");
        world.Partition().Available.Should().Be(
            new BigInteger(500),
            "500 of the declared 1000 is still spoken for — the give-back moved no counter");
    }

    // ── The world under test ─────────────────────────────────────────────────

    /// <summary>
    /// A buy-back with real arithmetic behind it: the reserve's own partition counters, the
    /// holon-backed queue, and a rail whose transfers actually change what the treasury holds.
    /// </summary>
    private sealed class BuyBackWorld
    {
        private readonly LedgerBackedReserve _reserve;
        private readonly InMemoryHolonStore _holons = new();
        private readonly FakeIdempotencyStore _idempotency = new();
        private readonly Mock<IProjectTreasury> _treasury = new();
        private readonly Mock<IBlockchainProvider> _provider = new();
        private readonly Mock<IBlockchainProviderFactory> _providers = new();

        public BuyBackWorld(long declaredBackstop)
        {
            _reserve = new LedgerBackedReserve(Chain, PlatformAsset, BackstopPartition, declaredBackstop);

            Options = new BuyBackOptions
            {
                Enabled = true,
                BackstopEnabled = true,
                QueueEnabled = true,
                BackstopPartition = BackstopPartition,
                // The share cap is not what these tests are about; leaving it at its shipped 25%
                // would refuse every request for a reason unrelated to the backstop.
                MaxRequestShareOfTreasuryPercent = 100m,
            };

            _treasury
                .Setup(treasury => treasury.FindAsync(
                    It.IsAny<ProjectTreasuryReference>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ProjectTreasuryReference reference, CancellationToken _) =>
                    AZOAResult<ProjectTreasuryAccount>.Success(new ProjectTreasuryAccount(
                        reference.NormalizedChainType,
                        reference.TenantId,
                        reference.NormalizedProjectReference,
                        TreasuryAddress,
                        "v1",
                        ProjectTreasuryStatus.Open)));

            _treasury
                .Setup(treasury => treasury.GetBalanceAsync(
                    It.IsAny<ProjectTreasuryReference>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<string>.Success(TreasuryBalance.ToString()));

            _treasury
                .Setup(treasury => treasury.GetLedgerAsync(
                    It.IsAny<ProjectTreasuryReference>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<ProjectTreasuryLedger>.Success(new ProjectTreasuryLedger()));

            _treasury
                .Setup(treasury => treasury.RecordMovementAsync(
                    It.IsAny<ProjectTreasuryMovementRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ProjectTreasuryMovementRequest request, CancellationToken _) =>
                {
                    TreasuryCredits.Add(request.Amount);
                    return AZOAResult<ProjectTreasuryMovementOutcome>.Success(
                        new ProjectTreasuryMovementOutcome(
                            request.AssetId,
                            request.Kind,
                            request.Amount,
                            new ProjectTreasuryAssetCounters(request.Amount, BigInteger.Zero, BigInteger.Zero),
                            1,
                            Replayed: false));
                });

            // A durable rail that actually moves value: the treasury's chain balance is what the
            // policy re-reads before it admits anything, so a transfer that does not change it
            // would let the test pass on a claim rather than on the money.
            _rail = new DurableRail(this);
            IValueTransferTransactionModule? module = _rail;
            _provider.Setup(provider => provider.TryGetModule(out module)).Returns(true);

            _providers.SetupGet(factory => factory.DefaultNetwork).Returns(ChainNetwork.Devnet);
            _providers
                .Setup(factory => factory.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
                .Returns(() => _provider.Object);
        }

        private readonly DurableRail _rail;

        public BuyBackOptions Options { get; }

        /// <summary>Signed transfers the rail was asked to prepare; one per request is the exact-wire invariant.</summary>
        public int PreparedTransfers => _rail.Prepared;

        /// <summary>Lets a broadcast that never confirmed finally confirm on chain.</summary>
        public void RailConfirmsWhatItTook() => RailBroadcastsWithoutConfirming = false;

        public BigInteger TreasuryBalance { get; private set; } = BigInteger.Zero;

        public List<BigInteger> TreasuryCredits { get; } = new();

        private bool RailRefuses { get; set; }

        private bool RailBroadcastsWithoutConfirming { get; set; }

        internal bool Refuses => RailRefuses;

        internal bool Pending => RailBroadcastsWithoutConfirming;

        internal void Land(BigInteger amount) => TreasuryBalance += amount;

        /// <summary>Makes every movement fail before anything is signed, so nothing is ever filled.</summary>
        public void RailRefusesToMove() => RailRefuses = true;

        /// <summary>
        /// Makes every movement take the transaction and never say whether it landed — the SUCCESS
        /// result carrying <c>OperationStatus.PendingConfirmationMarker</c> that
        /// <c>AlgorandProvider.BuildSignSubmitAsync</c> returns when its confirm poll runs out.
        /// The treasury balance deliberately does NOT move: whether it did is precisely what is
        /// unknown.
        /// </summary>
        public void RailTakesItAndSaysNothing() => RailBroadcastsWithoutConfirming = true;

        /// <summary>The movement claim for one request, read straight out of the ledger.</summary>
        public IdempotencyRecord? MoveClaim(string requestKey, string projectReference = "project-alpha")
            => _idempotency
                .GetAsync(BackstopKey(requestKey, projectReference) + ":move", CancellationToken.None)
                .GetAwaiter()
                .GetResult();

        /// <summary>Hands the set-aside back the way a retired place does.</summary>
        public Task<AZOAResult<bool>> GiveBackSetAside(
            string requestKey, long amount, string projectReference = "project-alpha")
        {
            var backstop = new ReserveToTreasuryBuyBackBackstop(
                _reserve, _treasury.Object, _providers.Object, _idempotency);

            return backstop.ReleaseReservationAsync(
                new BuyBackTopUpRequest(
                    ScopeFor(projectReference),
                    PlatformAsset,
                    BackstopPartition,
                    new BigInteger(amount),
                    BackstopKey(requestKey, projectReference)));
        }

        private static BuyBackQueueScope ScopeFor(string projectReference) =>
            new(Chain, TenantId, projectReference);

        private static string BackstopKey(string requestKey, string projectReference) =>
            BuyBackBackstopKeys.For(ScopeFor(projectReference), requestKey);

        public BuyBackLiquidityPolicy Policy()
        {
            var backstop = new ReserveToTreasuryBuyBackBackstop(
                _reserve, _treasury.Object, _providers.Object, _idempotency);
            var queue = new HolonBuyBackQueue(_holons, Options, logger: null, backstop: backstop);
            return new BuyBackLiquidityPolicy(Options, _treasury.Object, _reserve, queue, backstop);
        }

        public ExchangeRateEvaluation Sell(
            string requestKey,
            long platformAmount,
            string projectReference = "project-alpha",
            DateTimeOffset? now = null) =>
            new(Chain,
                ExchangeDirection.Sell,
                ProjectAsset,
                PlatformAsset,
                new BigInteger(platformAmount),
                new BigInteger(platformAmount),
                DeclaredRate: null,
                now ?? Now,
                TenantId: TenantId,
                ProjectReference: projectReference,
                SellerUserId: Guid.NewGuid(),
                RequestKey: requestKey);

        public ReservePartitionCounters Partition() => _reserve.Counters();

        /// <summary>Places still open across every project, read straight off the queue's own rows.</summary>
        public int OpenPlaces(DateTimeOffset? asOf = null)
        {
            var at = asOf ?? Now;
            return _holons.Rows.Count(row =>
                row.IsActive
                && row.AssetType == HolonBuyBackQueue.QueueAssetType
                && !IsExpired(row, at));
        }

        private bool IsExpired(IHolon row, DateTimeOffset asOf) =>
            Options.QueueEntryTtl > TimeSpan.Zero
            && row.Metadata.TryGetValue("enqueuedAt", out var text)
            && DateTimeOffset.TryParse(text, out var enqueuedAt)
            && asOf - enqueuedAt > Options.QueueEntryTtl;
    }

    /// <summary>Exact-wire rail edge: prepares once per key, confirms only what was sent, and moves the balance once.</summary>
    private sealed class DurableRail(BuyBackWorld world) : IValueTransferTransactionModule
    {
        private readonly HashSet<string> _sent = new(StringComparer.Ordinal);
        private readonly HashSet<string> _landed = new(StringComparer.Ordinal);

        public string CapabilityName => "value-transfer";
        public string ChainType => Chain;
        public int Prepared { get; private set; }

        public Task<AZOAResult<PreparedValueTransfer>> PrepareValueTransferAsync(ValueTransferIntent intent, CancellationToken ct)
        {
            if (world.Refuses)
                return Task.FromResult(AZOAResult<PreparedValueTransfer>.Failure("The rail refused this movement."));
            Prepared++;
            return Task.FromResult(AZOAResult<PreparedValueTransfer>.Success(
                new PreparedValueTransfer("rail-reference-" + Prepared, "c2lnbmVk", "genesis", intent)));
        }

        public Task SubmitValueTransferAsync(PreparedValueTransfer prepared, CancellationToken ct)
        {
            _sent.Add(prepared.TransactionId);
            return Task.CompletedTask;
        }

        public Task<AZOAResult<ChainConfirmation>> ObserveValueTransferAsync(PreparedValueTransfer prepared, CancellationToken ct)
        {
            if (!_sent.Contains(prepared.TransactionId) || world.Pending)
                return Task.FromResult(AZOAResult<ChainConfirmation>.Success(ChainConfirmation.Unknown));
            if (_landed.Add(prepared.TransactionId))
                world.Land(System.Numerics.BigInteger.Parse(prepared.Intent.QuantityBaseUnits));
            return Task.FromResult(AZOAResult<ChainConfirmation>.Success(ChainConfirmation.Confirmed));
        }
    }

    /// <summary>
    /// A platform reserve backed by the real <see cref="ReservePartitionCounters"/> arithmetic and
    /// the real replay window, so an oversubscribed draw is refused here for exactly the reason it
    /// is refused in production.
    /// </summary>
    private sealed class LedgerBackedReserve : IPlatformReserve
    {
        private readonly string _chain;
        private readonly string _assetId;
        private readonly string _partition;
        private readonly HashSet<string> _appliedKeys = new(StringComparer.Ordinal);
        private ReservePartitionCounters _counters;

        public LedgerBackedReserve(string chain, string assetId, string partition, long declared)
        {
            _chain = chain;
            _assetId = assetId;
            _partition = ReservePartitionCodec.Normalize(partition);
            _counters = ReservePartitionCounters.Declaring(new BigInteger(declared));
        }

        public ReservePartitionCounters Counters() => _counters;

        public Task<AZOAResult<ReserveAccount>> GetAccountAsync(string chainType, CancellationToken ct = default) =>
            Task.FromResult(AZOAResult<ReserveAccount>.Success(
                new ReserveAccount(_chain, ReserveAddress, IsFeePayer: true)));

        public Task<AZOAResult<PlatformReserveLedger>> GetLedgerAsync(
            string chainType, string assetId, CancellationToken ct = default) =>
            Task.FromResult(AZOAResult<PlatformReserveLedger>.Success(new PlatformReserveLedger
            {
                AssetId = _assetId,
                Chain = _chain,
                ReserveAddress = ReserveAddress,
                TotalSupply = "1000000",
                Partitions = ReservePartitionCodec.Encode(
                    new Dictionary<string, ReservePartitionCounters>(StringComparer.Ordinal)
                    {
                        [_partition] = _counters,
                    }),
            }));

        public Task<AZOAResult<ReserveDrawOutcome>> ReattributeReturnAsync(
            ReserveDrawRequest request, string toPartition, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AZOAResult<ReserveDrawOutcome>> DrawAsync(
            ReserveDrawRequest request, CancellationToken ct = default)
        {
            // The authority is not decoration: production asks this question inside DrawAsync, so a
            // seam that forgot to state one must fail here too.
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

        public Task<AZOAResult<string>> GetBalanceAsync(
            string chainType, string? assetId = null, CancellationToken ct = default) =>
            throw new NotSupportedException("The buy-back never reads the reserve's chain balance.");

        public Task<AZOAResult<T>> SignAsReserveAsync<T>(
            string chainType, SigningContext signingContext, Func<byte[], Task<T>> sign) =>
            throw new NotSupportedException("The buy-back signs through the rail provider.");

        public Task<AZOAResult<PlatformReserveLedger>> RegisterAssetAsync(
            ReserveAssetRegistration registration, CancellationToken ct = default) =>
            throw new NotSupportedException("The buy-back registers no assets.");

        public Task<AZOAResult<PlatformReserveLedger>> DeclarePartitionsAsync(
            string chainType,
            string assetId,
            IReadOnlyDictionary<string, BigInteger> declaredByPartition,
            ReserveDrawAuthority authority,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Partitions are declared by the operator, not by a sell.");
    }

    /// <summary>Holon persistence, in memory, filtered the way the queue's own query filters.</summary>
    private sealed class InMemoryHolonStore : IHolonStore
    {
        private readonly Dictionary<Guid, IHolon> _rows = new();

        public IReadOnlyCollection<IHolon> Rows => _rows.Values;

        public Task<AZOAResult<IHolon>> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(_rows.TryGetValue(id, out var row)
                ? AZOAResult<IHolon>.Success(row)
                : AZOAResult<IHolon>.Failure("No such holon."));

        public Task<AZOAResult<IEnumerable<IHolon>>> QueryAsync(
            HolonQueryRequest? query = null, CancellationToken ct = default)
        {
            var matches = _rows.Values.Where(row =>
                (query?.AssetType is null || row.AssetType == query.AssetType)
                && (query?.ChainId is null || row.ChainId == query.ChainId)
                && (query?.ProviderName is null || row.ProviderName == query.ProviderName)
                && (query?.IsActive is null || row.IsActive == query.IsActive));

            return Task.FromResult(AZOAResult<IEnumerable<IHolon>>.Success(matches.ToList()));
        }

        public Task<AZOAResult<IHolon>> UpsertAsync(IHolon holon, CancellationToken ct = default)
        {
            _rows[holon.Id] = holon;
            return Task.FromResult(AZOAResult<IHolon>.Success(holon));
        }

        public Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(AZOAResult<bool>.Success(_rows.Remove(id)));

        public Task<AZOAResult<bool>> TryReserveNftTransferAsync(
            Guid holonId, Guid sourceUserId, Guid targetUserId, string settlementKey,
            CancellationToken ct = default) =>
            throw new NotSupportedException("The buy-back queue moves no NFTs.");

        public Task<AZOAResult<bool>> FinalizeReservedNftTransferAsync(
            Guid holonId, Guid sourceUserId, Guid targetUserId, string settlementKey,
            CancellationToken ct = default) =>
            throw new NotSupportedException("The buy-back queue moves no NFTs.");

        public Task<AZOAResult<bool>> ReleaseNftTransferReservationAsync(
            Guid holonId, Guid sourceUserId, string settlementKey, CancellationToken ct = default) =>
            throw new NotSupportedException("The buy-back queue moves no NFTs.");
    }
}
