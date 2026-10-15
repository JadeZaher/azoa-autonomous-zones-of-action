// SPDX-License-Identifier: UNLICENSED
// L1.13 / OI-59 -- rung 2 of the reversal ladder: WHICH commitment a reversed payment funded.
//
// The stub this replaces refused on purpose, because giving back an arbitrary hold that a
// disputing sponsor happens to benefit from would rob whichever honest backer actually funded it.
// So every test here pins one half of that trade:
//   * it unwinds the commitment THIS payment funded, and leaves an identical-looking neighbour
//     alone;
//   * it refuses -- silently and without touching anything -- whenever the link is absent,
//     already settled, someone else's, or ambiguous;
//   * and it fails LOUD, not quiet, when the producer of the purchase record drifts, because a
//     silent "nothing was attributable" is indistinguishable from a platform loss.

using System.Numerics;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Fiat.OnRamp;
using AZOA.WebAPI.Services.Fiat.Reversal;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SurrealForge.Client.Query;
using Xunit;

namespace AZOA.WebAPI.Tests.Fiat;

public class ReversedPurchaseProvenanceTests
{
    private const string ChainType = "Algorand";

    /// <summary>The platform unit a card buys; what an on-ramp lot issues.</summary>
    private const string PlatformAssetId = "1001";

    /// <summary>What a sponsor exchanges those units for.</summary>
    private const string ProjectAssetId = "2002";

    private const string Partition = "sponsorship";
    private const string HolderAddress = "SPONSORALGOADDRESS";

    private static readonly Guid SubjectUserId = new("22222222222222222222222222222222");
    private static readonly Guid OtherUserId = new("33333333333333333333333333333333");

    private static readonly DateTimeOffset LotIssuedAt = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PurchasedAt = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    // ── The headline: this payment's commitment, and only this one ────────────

    [Fact]
    public async Task The_hold_this_payment_funded_is_unwound_and_no_other()
    {
        var harness = new Harness();
        harness.SeedLot("lot-1", quantity: "100000");
        harness.SeedPurchase("corr-1", spent: "100000", received: "50000");
        harness.SeedHold(ExchangePurchaseProvenance.ExchangeHoldIdPrefix + "corr-1", amount: "50000");

        // Same person, same asset, same amount, same partition, same day, still open: everything
        // a looser match would have grabbed. The exact link is what keeps it out.
        harness.SeedHold("worker-stake-7", amount: "50000");

        var claims = await harness.ListAsync("lot-1");

        claims.IsError.Should().BeFalse();
        claims.Result.Should().ContainSingle();
        var claim = claims.Result!.Single();
        claim.Kind.Should().Be(ReversedPurchaseKind.OpenHold);
        claim.HoldId.Should().Be("exchange:corr-1");
        claim.ChainType.Should().Be(ChainType);
        claim.SourceQuantityBaseUnits.Should().Be(new BigInteger(100000));
    }

    [Fact]
    public async Task A_lot_with_no_traceable_purchase_still_refuses()
    {
        var harness = new Harness();
        harness.SeedLot("lot-1", quantity: "100000");

        var claims = await harness.ListAsync("lot-1");

        claims.IsError.Should().BeFalse();
        claims.Result.Should().BeEmpty(
            "an unattributable payment becomes a recorded platform loss, never a guess");
    }

    [Fact]
    public async Task An_issuance_that_is_not_on_the_account_is_refused()
    {
        var harness = new Harness();
        harness.SeedLot("lot-1", quantity: "100000");
        harness.SeedPurchase("corr-1", spent: "100000", received: "50000");
        harness.SeedHold(ExchangePurchaseProvenance.ExchangeHoldIdPrefix + "corr-1", amount: "50000");

        var claims = await harness.ListAsync("lot-somebody-elses");

        claims.IsError.Should().BeFalse();
        claims.Result.Should().BeEmpty();
    }

    // ── The robbery guard ────────────────────────────────────────────────────

    [Fact]
    public async Task A_commitment_held_for_somebody_else_is_never_claimed()
    {
        var harness = new Harness();
        harness.SeedLot("lot-1", quantity: "100000");
        harness.SeedPurchase("corr-1", spent: "100000", received: "50000");

        // The exact link says this hold belongs to the purchase -- and the beneficiary says it
        // belongs to somebody else. The beneficiary wins: this is the whole point of rung 2.
        //
        // The subject is named as the FUNDER on purpose. A per-user hold read only ever returns
        // rows the person is beneficiary or funder of, so a hold that names them NOWHERE never
        // reaches ClaimForHold at all and this test would be asserting a property of its own
        // fixture rather than of the guard. Funding a commitment that pays out to someone else
        // is also the real shape of the robbery this guard refuses.
        harness.SeedHold(
            ExchangePurchaseProvenance.ExchangeHoldIdPrefix + "corr-1",
            amount: "50000",
            beneficiary: OtherUserId,
            funder: SubjectUserId);

        var claims = await harness.ListAsync("lot-1");

        claims.IsError.Should().BeFalse();
        claims.Result.Should().BeEmpty();
    }

    [Fact]
    public async Task A_commitment_somebody_else_funded_is_never_claimed()
    {
        var harness = new Harness();
        harness.SeedLot("lot-1", quantity: "100000");
        harness.SeedPurchase("corr-1", spent: "100000", received: "50000");
        harness.SeedHold(
            ExchangePurchaseProvenance.ExchangeHoldIdPrefix + "corr-1",
            amount: "50000",
            funder: OtherUserId);

        var claims = await harness.ListAsync("lot-1");

        claims.Result.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_matching_commitments_are_left_alone_rather_than_guessed_between()
    {
        var harness = new Harness();
        harness.SeedLot("lot-1", quantity: "100000");

        // No correlation, so the exact link is unavailable and only the shape match is left.
        harness.SeedPurchase(correlation: null, spent: "100000", received: "50000");
        harness.SeedHold("sponsor-a", amount: "50000");
        harness.SeedHold("sponsor-b", amount: "50000");

        var claims = await harness.ListAsync("lot-1");

        claims.IsError.Should().BeFalse();
        claims.Result.Should().BeEmpty(
            "unwinding the wrong one of two identical commitments is the harm this rung exists "
            + "to avoid");
    }

    [Fact]
    public async Task A_single_caller_named_commitment_is_matched_on_the_shape_the_exchange_opened_it_with()
    {
        var harness = new Harness();
        harness.SeedLot("lot-1", quantity: "100000");
        harness.SeedPurchase(correlation: null, spent: "100000", received: "50000");
        harness.SeedHold("sponsor-a", amount: "50000");

        var claims = await harness.ListAsync("lot-1");

        claims.Result.Should().ContainSingle().Which.HoldId.Should().Be("sponsor-a");
    }

    [Fact]
    public async Task A_commitment_drawn_from_a_different_partition_is_not_this_purchase()
    {
        var harness = new Harness();
        harness.SeedLot("lot-1", quantity: "100000");
        harness.SeedPurchase(correlation: null, spent: "100000", received: "50000");
        harness.SeedHold("worker-stake-7", amount: "50000", partition: "quest-rewards");

        var claims = await harness.ListAsync("lot-1");

        // No hold matched, so the fallback is the person's own account -- never the stake hold.
        claims.Result.Should().ContainSingle()
            .Which.Kind.Should().Be(ReversedPurchaseKind.IssuedAsset);
    }

    [Fact]
    public async Task A_commitment_that_has_already_settled_is_not_taken_back()
    {
        var harness = new Harness();
        harness.SeedLot("lot-1", quantity: "100000");
        harness.SeedPurchase("corr-1", spent: "100000", received: "50000");
        harness.SeedHold(
            ExchangePurchaseProvenance.ExchangeHoldIdPrefix + "corr-1",
            amount: "50000",
            state: nameof(HoldState.Released));

        var claims = await harness.ListAsync("lot-1");

        claims.Result.Should().BeEmpty("those units are with the beneficiary, not in the holding account");
    }

    // ── Straight delivery: the units went into the person's own account ───────

    [Fact]
    public async Task A_purchase_delivered_straight_claims_the_asset_it_bought()
    {
        var harness = new Harness();
        harness.SeedLot("lot-1", quantity: "100000");
        harness.SeedPurchase("corr-1", spent: "100000", received: "50000");

        var claims = await harness.ListAsync("lot-1");

        var claim = claims.Result.Should().ContainSingle().Subject;
        claim.Kind.Should().Be(ReversedPurchaseKind.IssuedAsset);
        claim.AssetId.Should().Be(ProjectAssetId);
        claim.AssetQuantityBaseUnits.Should().Be(new BigInteger(50000));
        claim.HolderAddress.Should().Be(HolderAddress);
    }

    // ── FIFO: which lot funded which purchase ────────────────────────────────

    [Fact]
    public async Task A_purchase_made_before_this_payment_landed_is_not_attributed_to_it()
    {
        var harness = new Harness();
        harness.SeedLot("lot-early", quantity: "40000", issuedAt: LotIssuedAt);
        harness.SeedLot("lot-late", quantity: "60000", issuedAt: LotIssuedAt.AddDays(2));

        // Spent between the two issuances: only the earlier lot's units existed.
        harness.SeedPurchase("corr-early", spent: "40000", received: "40000", at: LotIssuedAt.AddDays(1));

        var claims = await harness.ListAsync("lot-late");

        claims.Result.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_the_share_this_payment_funded_is_taken_back()
    {
        var harness = new Harness();
        harness.SeedLot("lot-early", quantity: "40000", issuedAt: LotIssuedAt);
        harness.SeedLot("lot-late", quantity: "60000", issuedAt: LotIssuedAt.AddDays(1));

        // One purchase drawing on both lots: 40_000 of it is the earlier payment's, 60_000 this one's.
        harness.SeedPurchase(
            "corr-1", spent: "100000", received: "100000", at: LotIssuedAt.AddDays(2));

        var claims = await harness.ListAsync("lot-late");

        var claim = claims.Result.Should().ContainSingle().Subject;
        claim.SourceQuantityBaseUnits.Should().Be(new BigInteger(60000));
        claim.AssetQuantityBaseUnits.Should().Be(new BigInteger(60000),
            "the clawback is pro-rated to the share this card actually paid for");
    }

    [Fact]
    public async Task Units_a_cash_out_already_took_cannot_also_have_bought_something()
    {
        var harness = new Harness();
        harness.SeedLot("lot-1", quantity: "100000", consumed: "100000");
        harness.SeedPurchase("corr-1", spent: "100000", received: "50000");
        harness.SeedHold(ExchangePurchaseProvenance.ExchangeHoldIdPrefix + "corr-1", amount: "50000");

        var claims = await harness.ListAsync("lot-1");

        claims.Result.Should().BeEmpty();
    }

    [Fact]
    public async Task An_already_reversed_lot_still_holds_its_place_in_the_order()
    {
        var harness = new Harness();
        harness.SeedLot(
            "lot-early", quantity: "40000", issuedAt: LotIssuedAt,
            status: OnRampLotStatuses.Reversed);
        harness.SeedLot("lot-late", quantity: "60000", issuedAt: LotIssuedAt.AddDays(1));
        harness.SeedPurchase("corr-1", spent: "40000", received: "40000", at: LotIssuedAt.AddDays(2));

        var claims = await harness.ListAsync("lot-late");

        // The reversed lot funded that purchase and was unwound on its own reversal. If it had
        // dropped out of the frame, this one would take the purchase back a second time.
        claims.Result.Should().BeEmpty();
    }

    // ── What is not a purchase ───────────────────────────────────────────────

    [Fact]
    public async Task A_failed_exchange_spent_nothing()
    {
        var harness = new Harness();
        harness.SeedLot("lot-1", quantity: "100000");
        harness.SeedPurchase("corr-1", spent: "100000", received: "50000", status: OperationStatus.Failed);

        var claims = await harness.ListAsync("lot-1");

        claims.Result.Should().BeEmpty("a failed exchange is compensated by the exchange itself");
    }

    [Fact]
    public async Task Selling_is_not_a_purchase_this_payment_funded()
    {
        var harness = new Harness();
        harness.SeedLot("lot-1", quantity: "100000");
        harness.SeedPurchase("corr-1", spent: "100000", received: "50000", direction: "Sell");

        var claims = await harness.ListAsync("lot-1");

        claims.Result.Should().BeEmpty();
    }

    [Fact]
    public async Task A_purchase_of_a_different_asset_is_not_this_payment()
    {
        var harness = new Harness();
        harness.SeedLot("lot-1", quantity: "100000");
        harness.SeedPurchase("corr-1", spent: "100000", received: "50000", spentAssetId: "9999");

        var claims = await harness.ListAsync("lot-1");

        claims.Result.Should().BeEmpty();
    }

    // ── Drift is loud ────────────────────────────────────────────────────────

    [Fact]
    public async Task An_exchange_row_that_no_longer_records_its_direction_is_an_error_not_a_silence()
    {
        var harness = new Harness();
        harness.SeedLot("lot-1", quantity: "100000");
        var purchase = harness.SeedPurchase("corr-1", spent: "100000", received: "50000");
        purchase.Parameters.Remove("ExchangeDirection");

        var claims = await harness.ListAsync("lot-1");

        // A rename on the producing side must escalate to a person, never quietly turn every
        // reversal into an absorbed platform loss.
        claims.IsError.Should().BeTrue();
        claims.Message.Should().Contain(purchase.Id.ToString());
    }

    [Fact]
    public async Task An_unreadable_lot_history_is_an_error_not_an_empty_answer()
    {
        var harness = new Harness();
        harness.Lots.FailWith = "Surreal is unreachable.";

        var claims = await harness.ListAsync("lot-1");

        claims.IsError.Should().BeTrue();
    }

    // ── The wiring: rung 2 gets the real link, not the refusal ───────────────

    [Fact]
    public void AddFiatReversalPipeline_registers_the_real_provenance_source()
    {
        var services = ReversalCollaborators();

        ReversalRegistration.AddFiatReversalPipeline(services);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Resolved, not merely inspected: the stub took no constructor arguments and the real one
        // takes three, so a registration that compiles but cannot be built fails here.
        scope.ServiceProvider.GetRequiredService<IReversedPurchaseProvenance>()
            .Should().BeOfType<ExchangePurchaseProvenance>(
                "OI-59 is closed by what the container hands the ladder, not by the class existing");
    }

    [Fact]
    public void A_host_that_registers_its_own_provenance_first_keeps_it()
    {
        var services = ReversalCollaborators();
        services.AddSingleton<IReversedPurchaseProvenance, UnavailableReversedPurchaseProvenance>();

        ReversalRegistration.AddFiatReversalPipeline(services);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IReversedPurchaseProvenance>()
            .Should().BeOfType<UnavailableReversedPurchaseProvenance>();
    }

    /// <summary>Everything <c>AddFiatReversalPipeline</c> checks for, as mocks.</summary>
    private static ServiceCollection ReversalCollaborators()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddSingleton(_ => Mock.Of<IHttpClientFactory>());
        services.AddScoped(_ => Mock.Of<ISurrealExecutor>());
        services.AddScoped(_ => Mock.Of<IOnRampLotLedger>());
        services.AddScoped(_ => Mock.Of<IPlatformReserve>());
        services.AddScoped(_ => Mock.Of<IPlatformHold>());
        services.AddScoped(_ => Mock.Of<IHoldLedgerStore>());
        services.AddScoped(_ => Mock.Of<IBlockchainOperationStore>());
        services.AddScoped(_ => Mock.Of<IIdempotencyStore>());
        services.AddScoped(_ => Mock.Of<ISagaCoordinator>());
        services.AddScoped(_ => Mock.Of<IBlockchainProviderFactory>());
        services.AddScoped(_ => Mock.Of<IWalletStore>());
        return services;
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public FakeLotLookup Lots { get; } = new();

        public FakeOperationStore Operations { get; } = new();

        public FakeHoldStore Holds { get; } = new();

        public Task<AZOAResult<IReadOnlyList<ReversedPurchaseClaim>>> ListAsync(string lotId)
            => new ExchangePurchaseProvenance(
                    Lots, Operations, Holds,
                    NullLogger<ExchangePurchaseProvenance>.Instance)
                .ListForLotAsync(lotId, SubjectUserId);

        public OnRampLot SeedLot(
            string id,
            string quantity,
            string consumed = "0",
            DateTimeOffset? issuedAt = null,
            string? status = null)
        {
            var lot = new OnRampLot
            {
                Id = id,
                SubjectUserId = SubjectUserId.ToString("N"),
                ChainType = ChainType,
                AssetId = PlatformAssetId,
                QuantityBaseUnits = quantity,
                ConsumedBaseUnits = consumed,
                Status = status ?? OnRampLotStatuses.Issued,
                IssuedAt = issuedAt ?? LotIssuedAt,
            };
            Lots.Rows.Add(lot);
            return lot;
        }

        public BlockchainOperation SeedPurchase(
            string? correlation,
            string spent,
            string received,
            DateTimeOffset? at = null,
            string? status = null,
            string direction = "Buy",
            string? spentAssetId = null)
        {
            var row = new BlockchainOperation
            {
                Id = Guid.NewGuid(),
                UserId = SubjectUserId,
                IdempotencyKey = correlation,
                OperationType = AZOA.WebAPI.Managers.ReserveExchangeManager.ChainOperationType,
                Status = status ?? OperationStatus.Exchanged,
                CreatedDate = (at ?? PurchasedAt).UtcDateTime,
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["WalletAddress"] = HolderAddress,
                    ["ChainType"] = ChainType,
                    ["Amount"] = spent,
                    ["SourceTokenId"] = spentAssetId ?? PlatformAssetId,
                    ["TargetTokenId"] = ProjectAssetId,
                    ["AmountOut"] = received,
                    ["ExchangeDirection"] = direction,
                    ["ReservePartition"] = Partition,
                },
            };
            Operations.Rows.Add(row);
            return row;
        }

        public HoldLedger SeedHold(
            string holdId,
            string amount,
            Guid? beneficiary = null,
            Guid? funder = null,
            string? state = null,
            string? partition = null,
            DateTimeOffset? createdAt = null)
        {
            var hold = new HoldLedger
            {
                Id = HoldLedger.RecordIdFor(ChainType, "Devnet", holdId),
                HoldId = holdId,
                Chain = ChainType,
                Network = "Devnet",
                HoldingAddress = "RESERVEALGOADDRESS",
                AssetId = ProjectAssetId,
                Amount = amount,
                ReservePartition = partition ?? Partition,
                FundingSource = nameof(HoldFundingSource.Reserve),
                BeneficiaryUserId = (beneficiary ?? SubjectUserId).ToString("N"),
                FunderUserId = funder?.ToString("N"),
                State = state ?? nameof(HoldState.Held),
                CreatedAt = createdAt ?? PurchasedAt,
            };
            Holds.Rows.Add(hold);
            return hold;
        }
    }

    /// <summary>In-memory lot lookup; only the subject read this source uses matters.</summary>
    private sealed class FakeLotLookup : IOnRampLotLookup
    {
        public List<OnRampLot> Rows { get; } = new();

        public string? FailWith { get; set; }

        public Task<AZOAResult<OnRampLot?>> GetByPaymentIntentAsync(
            string paymentIntentId, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<OnRampLot?> { Result = null });

        public Task<AZOAResult<OnRampLot?>> GetByChargeAsync(string chargeId, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<OnRampLot?> { Result = null });

        public Task<AZOAResult<IReadOnlyList<OnRampLot>>> ListForSubjectAsync(
            Guid subjectUserId, CancellationToken ct = default)
            => Task.FromResult(FailWith is null
                ? AZOAResult<IReadOnlyList<OnRampLot>>.Success(
                    Rows.OrderBy(row => row.IssuedAt).ToList())
                : AZOAResult<IReadOnlyList<OnRampLot>>.Failure(FailWith));
    }

    /// <summary>In-memory operation store; the provenance only ever pages one user's window.</summary>
    private sealed class FakeOperationStore : IBlockchainOperationStore
    {
        public List<BlockchainOperation> Rows { get; } = new();

        public Task<AZOAResult<IReadOnlyList<IBlockchainOperation>>> QueryAsync(
            BlockchainOperationQuery query, CancellationToken ct = default)
        {
            var page = Rows
                .Where(row => row.UserId == query.UserId)
                .Where(row => !query.From.HasValue || row.CreatedDate >= query.From.Value.UtcDateTime)
                .OrderByDescending(row => row.CreatedDate)
                .Skip(query.Skip)
                .Take(query.Take)
                .Cast<IBlockchainOperation>()
                .ToList();

            return Task.FromResult(
                AZOAResult<IReadOnlyList<IBlockchainOperation>>.Success(page));
        }

        public Task<AZOAResult<IBlockchainOperation>> GetByIdAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AZOAResult<IBlockchainOperation>> GetByIdempotencyKeyAsync(
            string idempotencyKey, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AZOAResult<IEnumerable<IBlockchainOperation>>> GetByUserAsync(
            Guid userId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AZOAResult<IBlockchainOperation>> UpsertAsync(
            IBlockchainOperation operation, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>In-memory hold ledger; the provenance reads one person's holds and nothing else.</summary>
    private sealed class FakeHoldStore : IHoldLedgerStore
    {
        public List<HoldLedger> Rows { get; } = new();

        public Task<AZOAResult<IReadOnlyList<HoldLedger>>> ListForUserAsync(
            string userId, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<HoldLedger>>.Success(
                Rows.Where(row =>
                        string.Equals(row.BeneficiaryUserId, userId, StringComparison.Ordinal)
                        || string.Equals(row.FunderUserId, userId, StringComparison.Ordinal))
                    .OrderByDescending(row => row.CreatedAt)
                    .ToList()));

        public Task<AZOAResult<HoldLedger?>> GetAsync(string recordId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AZOAResult<HoldLedgerClaim>> CreateIfAbsentAsync(
            HoldLedger row, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AZOAResult<HoldLedger>> UpdateIfVersionAsync(
            HoldLedger row, long expectedVersion, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AZOAResult<IReadOnlyList<HoldLedger>>> ListDueAsync(
            string chain, string network, DateTimeOffset asOf, int limit, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
