// SPDX-License-Identifier: UNLICENSED
// Rung 2 of the reversal ladder: WHICH settled purchase a reversed payment funded. Attribution
// walks the person's lots and their own settled exchange sources oldest-first; a held purchase
// names its exact liability, so nothing is ever matched by time window or shape.

using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Fiat.OnRamp;
using AZOA.WebAPI.Services.Fiat.Reversal;
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
    private const string PlatformAssetId = "1001";
    private const string ProjectAssetId = "2002";
    private const string HolderAddress = "SPONSORALGOADDRESS";

    private static readonly Guid SubjectUserId = new("22222222222222222222222222222222");
    private static readonly Guid TenantUserId = new("44444444444444444444444444444444");
    private static readonly DateTimeOffset LotIssuedAt = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PurchasedAt = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_held_purchase_this_payment_funded_names_its_exact_source_and_liability()
    {
        var harness = new Harness();
        harness.SeedLot("lot-1", quantity: "100000");
        var source = harness.SeedPurchase("source-1", spent: "100000", received: "50000", held: true);

        var claims = await harness.ListAsync("lot-1");

        var claim = claims.Result.Should().ContainSingle().Subject;
        claim.Kind.Should().Be(ReversedPurchaseKind.OpenHold);
        claim.SourceId.Should().Be(source.Id);
        claim.HoldId.Should().Be("hold-source-1");
        claim.SourceQuantityBaseUnits.Should().Be(new BigInteger(100000));
    }

    [Fact]
    public async Task A_purchase_delivered_straight_claims_the_asset_it_bought_from_the_participant_wallet()
    {
        var harness = new Harness();
        harness.SeedLot("lot-1", quantity: "100000");
        harness.SeedPurchase("source-1", spent: "100000", received: "50000", held: false);

        var claim = (await harness.ListAsync("lot-1")).Result.Should().ContainSingle().Subject;

        claim.Kind.Should().Be(ReversedPurchaseKind.IssuedAsset);
        claim.AssetId.Should().Be(ProjectAssetId);
        claim.AssetQuantityBaseUnits.Should().Be(new BigInteger(50000));
        claim.HolderAddress.Should().Be(HolderAddress);
        claim.SourceId.Should().Be("source-1");
    }

    [Theory]
    [InlineData("no-purchase")]
    [InlineData("other-lot")]
    [InlineData("before-lot")]
    [InlineData("consumed")]
    [InlineData("sale")]
    [InlineData("other-asset")]
    public async Task Nothing_untraceable_to_this_payment_is_claimed(string shape)
    {
        var harness = new Harness();
        harness.SeedLot("lot-early", quantity: "40000", issuedAt: LotIssuedAt, consumed: shape == "consumed" ? "40000" : "0");
        harness.SeedLot("lot-late", quantity: "60000", issuedAt: LotIssuedAt.AddDays(2));
        if (shape != "no-purchase")
            harness.SeedPurchase("source-1", spent: "40000", received: "40000", held: true, at: LotIssuedAt.AddDays(1),
                sale: shape == "sale", spentAssetId: shape == "other-asset" ? "9999" : null);
        var lot = shape is "consumed" or "sale" or "other-asset" or "no-purchase" ? "lot-early"
            : shape == "other-lot" ? "lot-somebody-elses" : "lot-late";

        var claims = await harness.ListAsync(lot);

        claims.IsError.Should().BeFalse();
        claims.Result.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_the_share_this_payment_funded_is_attributed_across_two_lots()
    {
        var harness = new Harness();
        harness.SeedLot("lot-early", quantity: "40000", issuedAt: LotIssuedAt);
        harness.SeedLot("lot-late", quantity: "60000", issuedAt: LotIssuedAt.AddDays(1));
        harness.SeedPurchase("source-1", spent: "100000", received: "100000", held: false, at: LotIssuedAt.AddDays(2));

        var claim = (await harness.ListAsync("lot-late")).Result.Should().ContainSingle().Subject;

        claim.SourceQuantityBaseUnits.Should().Be(new BigInteger(60000));
        claim.AssetQuantityBaseUnits.Should().Be(new BigInteger(60000));
    }

    [Fact]
    public async Task An_already_reversed_lot_still_holds_its_place_in_the_order()
    {
        var harness = new Harness();
        harness.SeedLot("lot-early", quantity: "40000", issuedAt: LotIssuedAt, status: OnRampLotStatuses.Reversed);
        harness.SeedLot("lot-late", quantity: "60000", issuedAt: LotIssuedAt.AddDays(1));
        harness.SeedPurchase("source-1", spent: "40000", received: "40000", held: true, at: LotIssuedAt.AddDays(2));

        (await harness.ListAsync("lot-late")).Result.Should().BeEmpty(
            "the reversed lot funded that purchase; dropping it from the frame would recover it twice");
    }

    [Fact]
    public async Task A_released_held_purchase_is_clawed_back_from_whoever_received_it()
    {
        var harness = new Harness();
        harness.SeedLot("lot-1", quantity: "100000");
        var source = harness.SeedPurchase("source-1", spent: "100000", received: "50000", held: true);
        harness.Liabilities[source.Id] = new ExchangePurchaseLiability("remainder", "Released", "30000", "RECIPIENTADDRESS");

        var claim = (await harness.ListAsync("lot-1")).Result.Should().ContainSingle().Subject;

        claim.Kind.Should().Be(ReversedPurchaseKind.IssuedAsset, "released units are no longer in custody to refund");
        claim.HolderAddress.Should().Be("RECIPIENTADDRESS");
        claim.AssetQuantityBaseUnits.Should().Be(new BigInteger(30000), "never more than the release actually delivered");
        claim.SourceId.Should().Be(source.Id);
    }

    [Fact]
    public async Task A_partially_reversed_lot_keeps_its_full_place_in_the_order()
    {
        var harness = new Harness();
        harness.SeedLot("lot-early", quantity: "40000", issuedAt: LotIssuedAt, consumed: "10000", reversed: "10000");
        harness.SeedLot("lot-late", quantity: "60000", issuedAt: LotIssuedAt.AddDays(1));
        harness.SeedPurchase("source-1", spent: "40000", received: "40000", held: true, at: LotIssuedAt.AddDays(2));

        var early = (await harness.ListAsync("lot-early")).Result.Should().ContainSingle().Subject;

        early.SourceQuantityBaseUnits.Should().Be(new BigInteger(40000));
        (await harness.ListAsync("lot-late")).Result.Should().BeEmpty(
            "a reversal of the early lot must not shift its purchase onto the next payment");
    }

    [Fact]
    public async Task An_unreadable_lot_history_is_an_error_not_an_empty_answer()
    {
        var harness = new Harness();
        harness.Lots.FailWith = "Surreal is unreachable.";

        (await harness.ListAsync("lot-1")).IsError.Should().BeTrue();
    }

    [Fact]
    public void AddFiatReversalPipeline_registers_the_source_backed_provenance()
    {
        var services = ReversalCollaborators();

        ReversalRegistration.AddFiatReversalPipeline(services);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IReversedPurchaseProvenance>().Should().BeOfType<ExchangePurchaseProvenance>();
    }

    [Fact]
    public void A_host_that_registers_its_own_provenance_first_keeps_it()
    {
        var services = ReversalCollaborators();
        services.AddSingleton<IReversedPurchaseProvenance, UnavailableReversedPurchaseProvenance>();

        ReversalRegistration.AddFiatReversalPipeline(services);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IReversedPurchaseProvenance>().Should().BeOfType<UnavailableReversedPurchaseProvenance>();
    }

    private static ServiceCollection ReversalCollaborators()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddSingleton(_ => Mock.Of<IHttpClientFactory>());
        services.AddScoped(_ => Mock.Of<ISurrealExecutor>());
        services.AddScoped(_ => Mock.Of<IOnRampLotLedger>());
        services.AddScoped(_ => Mock.Of<IPlatformReserve>());
        services.AddScoped(_ => Mock.Of<IExchangeHoldSourceStore>());
        services.AddScoped(_ => Mock.Of<IIdempotencyStore>());
        services.AddScoped(_ => Mock.Of<ISagaCoordinator>());
        services.AddScoped(_ => Mock.Of<IBlockchainProviderFactory>());
        services.AddScoped(_ => Mock.Of<IWalletStore>());
        return services;
    }

    private sealed class Harness
    {
        public FakeLotLookup Lots { get; } = new();

        public List<ExchangeHoldSource> Sources { get; } = new();

        public Dictionary<string, ExchangePurchaseLiability> Liabilities { get; } = new();

        public Task<AZOAResult<IReadOnlyList<ReversedPurchaseClaim>>> ListAsync(string lotId)
        {
            var store = new Mock<IExchangeHoldSourceStore>();
            store.Setup(item => item.ListParticipantPurchasesAsync(SubjectUserId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Sources.Where(source => source.State == "Completed").ToList());
            store.Setup(item => item.GetPurchaseLiabilityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string sourceId, CancellationToken _) => Liabilities.GetValueOrDefault(sourceId));
            return new ExchangePurchaseProvenance(Lots, store.Object, NullLogger<ExchangePurchaseProvenance>.Instance)
                .ListForLotAsync(lotId, SubjectUserId);
        }

        public void SeedLot(string id, string quantity, string consumed = "0", DateTimeOffset? issuedAt = null, string? status = null,
            string reversed = "0")
            => Lots.Rows.Add(new OnRampLot
            {
                Id = id, SubjectUserId = SubjectUserId.ToString("N"), ChainType = ChainType, AssetId = PlatformAssetId,
                QuantityBaseUnits = quantity, ConsumedBaseUnits = consumed, ReversedBaseUnits = reversed,
                Status = status ?? OnRampLotStatuses.Issued,
                IssuedAt = issuedAt ?? LotIssuedAt,
            });

        public ExchangeHoldSource SeedPurchase(string id, string spent, string received, bool held, DateTimeOffset? at = null,
            bool sale = false, string? spentAssetId = null)
        {
            var asset = new HoldAssetIdentity(ChainType, ChainNetwork.Devnet, "genesis", ProjectAssetId, "RESERVEALGOADDRESS");
            var opening = held && !sale
                ? new HoldCommandIntent(1, "exchange:" + id, "Open", "hold-" + id, TenantUserId, TenantUserId, null, TenantUserId, asset,
                    new(asset, received, "Reserve", SubjectUserId, null, "sponsorship", PurchasedAt.AddDays(30), false, null))
                : null;
            var units = opening is null ? new ExchangeUnitsCommitment(sale ? "Return" : "Issue", asset, received, "sponsorship") : null;
            var intent = new ExchangeHoldSourceIntent(1, id, Guid.NewGuid(), TenantUserId, SubjectUserId, "{}", opening,
                spentAssetId ?? PlatformAssetId, spent, "project", "treasury",
                new(Guid.NewGuid(), SubjectUserId, HolderAddress, ChainType), "10000", Units: units);
            var source = new ExchangeHoldSource
            {
                Id = id, State = "Completed", ParticipantUserId = SubjectUserId.ToString("N"),
                IntentJson = JsonSerializer.Serialize(intent), HoldRecordId = opening is null ? null : "hold-record-" + id,
                UpdatedAt = at ?? PurchasedAt,
            };
            Sources.Add(source);
            return source;
        }
    }

    private sealed class FakeLotLookup : IOnRampLotLookup
    {
        public List<OnRampLot> Rows { get; } = new();

        public string? FailWith { get; set; }

        public Task<AZOAResult<OnRampLot?>> GetByPaymentIntentAsync(string paymentIntentId, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<OnRampLot?> { Result = null });

        public Task<AZOAResult<OnRampLot?>> GetByChargeAsync(string chargeId, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<OnRampLot?> { Result = null });

        public Task<AZOAResult<IReadOnlyList<OnRampLot>>> ListForSubjectAsync(Guid subjectUserId, CancellationToken ct = default)
            => Task.FromResult(FailWith is null
                ? AZOAResult<IReadOnlyList<OnRampLot>>.Success(Rows.OrderBy(row => row.IssuedAt).ToList())
                : AZOAResult<IReadOnlyList<OnRampLot>>.Failure(FailWith));
    }
}
