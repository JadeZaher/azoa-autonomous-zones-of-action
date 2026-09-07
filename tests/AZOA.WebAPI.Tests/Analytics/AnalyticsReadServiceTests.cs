// SPDX-License-Identifier: UNLICENSED

using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Analytics;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Analytics;

/// <summary>
/// The invariant under test throughout: an unpriced row must read back VISIBLY unpriced.
/// See <c>Services/Analytics/AGENTS.md</c> §unpriced-representation.
/// </summary>
public sealed class AnalyticsReadServiceTests
{
    private static readonly Guid SubjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime Settled = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── Pricing is three-way, never two-way ───────────────────────────────────

    [Fact]
    public async Task GetTransactionsAsync_UnstampedOperation_ReportsNeverPricedWithNoValue()
    {
        var service = ServiceReturning(Operation());

        var result = await service.GetTransactionsAsync(SubjectId, new AnalyticsQueryRequest());

        result.IsError.Should().BeFalse(result.Message);
        var entry = result.Result!.Items.Should().ContainSingle().Subject;
        entry.Pricing.Should().Be(PricingStatus.NeverPriced);
        entry.Value.Should().BeNull();
        result.Result.UnpricedEntryCount.Should().Be(1);
    }

    [Fact]
    public async Task GetTransactionsAsync_StampedAtRateZero_StaysPricedAndNeverCollapsesToNeverPriced()
    {
        // A real source really did quote zero. That is a DIFFERENT fact from never having
        // been priced, and the whole five-column group exists to keep them different.
        var service = ServiceReturning(Operation(stamp: Stamp(rate: "0", minorUnits: "0")));

        var result = await service.GetTransactionsAsync(SubjectId, new AnalyticsQueryRequest());

        var entry = result.Result!.Items.Should().ContainSingle().Subject;
        entry.Pricing.Should().Be(PricingStatus.Priced);
        entry.Value!.Rate.Should().Be("0");
        entry.Value.MinorUnits.Should().Be("0");
        result.Result.UnpricedEntryCount.Should().Be(0);
    }

    [Fact]
    public async Task GetTransactionsAsync_PartialPersistedStamp_ReadsBackUnpriced()
    {
        // FromPersisted refuses a partial stamp, so the projection never sees a half-truth.
        var partial = ValueStamp.FromPersisted("USD", "25000", rate: null, rateAsOf: null, rateSource: "test");
        partial.Should().BeNull();

        var service = ServiceReturning(Operation(stamp: partial));

        var result = await service.GetTransactionsAsync(SubjectId, new AnalyticsQueryRequest());

        var entry = result.Result!.Items.Should().ContainSingle().Subject;
        entry.Pricing.Should().Be(PricingStatus.NeverPriced);
        entry.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetTransactionsAsync_StampedOperation_CarriesEveryStampComponentThrough()
    {
        var service = ServiceReturning(Operation(stamp: Stamp()));

        var result = await service.GetTransactionsAsync(SubjectId, new AnalyticsQueryRequest());

        var value = result.Result!.Items.Single().Value!;
        value.CurrencyCode.Should().Be("USD");
        value.MinorUnits.Should().Be("25000");
        value.Rate.Should().Be("250");
        value.RateSource.Should().Be("test-source");
        value.RateAsOf.Should().Be(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    // ── Quantity is three-way too: an unknown amount is never a zero ──────────

    [Fact]
    public async Task GetTransactionsAsync_OperationWithNoRecordedAmount_ReadsBackUnknown_NeverZero()
    {
        // The pricing failure mode, moved to the quantity axis: rendering "the log does not
        // say" as the literal "0" puts a fabricated quantity on a tax record.
        var service = ServiceReturning(Operation(amount: null));

        var result = await service.GetTransactionsAsync(SubjectId, new AnalyticsQueryRequest());

        var entry = result.Result!.Items.Should().ContainSingle().Subject;
        entry.AmountStatus.Should().Be(QuantityStatus.Unknown);
        entry.Amount.Should().BeNull("an unknown quantity is absent, not zero");
        result.Result.UnknownAmountEntryCount.Should().Be(1);
    }

    [Fact]
    public async Task GetTransactionsAsync_UnreadableAmount_ReadsBackUnknown_NeverZero()
    {
        var operation = Operation(amount: null);
        operation.Parameters["Amount"] = "not-a-number";

        var result = await ServiceReturning(operation)
            .GetTransactionsAsync(SubjectId, new AnalyticsQueryRequest());

        var entry = result.Result!.Items.Should().ContainSingle().Subject;
        entry.AmountStatus.Should().Be(QuantityStatus.Unknown);
        entry.Amount.Should().BeNull();
    }

    [Fact]
    public async Task GetTransactionsAsync_RecordedZeroAmount_StaysAKnownZero()
    {
        // The same distinction the price axis keeps between "never priced" and a real quote
        // of zero: a movement the log really records as zero is a fact, not a gap.
        var service = ServiceReturning(Operation(amount: "0"));

        var result = await service.GetTransactionsAsync(SubjectId, new AnalyticsQueryRequest());

        var entry = result.Result!.Items.Should().ContainSingle().Subject;
        entry.AmountStatus.Should().Be(QuantityStatus.Known);
        entry.Amount.Should().Be("0");
        result.Result.UnknownAmountEntryCount.Should().Be(0);
    }

    [Fact]
    public async Task GetPositionsAsync_EntryWithNoRecordedAmount_IsLeftOutOfTheTotalAndCounted()
    {
        var service = ServiceReturning(
            Operation(type: "Mint", status: OperationStatus.Minted, amount: "100"),
            Operation(type: "Mint", status: OperationStatus.Minted, amount: null));

        var result = await service.GetPositionsAsync(SubjectId, new AnalyticsQueryRequest());

        var position = result.Result!.Items.Should().ContainSingle().Subject;
        position.Quantity.Should().Be("100", "an unknown quantity is not a movement of zero");
        position.UnknownAmountEntryCount.Should().Be(1);
        position.IsQuantityComplete.Should().BeFalse(
            "a total that silently omitted an entry must not claim to cover the window");
    }

    // ── Stampability is a fact, not a gap ─────────────────────────────────────

    [Theory]
    [InlineData("Mint", OperationStatus.Minted, true)]
    [InlineData("Burn", OperationStatus.Burned, true)]
    [InlineData("Transfer", OperationStatus.Transferred, true)]
    [InlineData("Swap", OperationStatus.Swapped, false)]
    [InlineData("Exchange", OperationStatus.Exchanged, false)]
    [InlineData("DeployContract", OperationStatus.Deployed, false)]
    [InlineData("Mint", OperationStatus.Failed, false)]
    [InlineData("Mint", OperationStatus.AwaitingSignature, false)]
    [InlineData("Mint", OperationStatus.Pending, false)]
    public async Task GetTransactionsAsync_IsStampable_MirrorsTheValueStampRule(
        string operationType,
        string status,
        bool expected)
    {
        var service = ServiceReturning(Operation(type: operationType, status: status));

        var result = await service.GetTransactionsAsync(SubjectId, new AnalyticsQueryRequest());

        result.Result!.Items.Single().IsStampable.Should().Be(expected);
    }

    [Fact]
    public async Task GetPositionsAsync_UnstampableSwap_IsCountedSeparatelyFromAPricingGap()
    {
        var service = ServiceReturning(
            Operation(type: "Swap", status: OperationStatus.Swapped),
            Operation(type: "Mint", status: OperationStatus.Minted));

        var result = await service.GetPositionsAsync(SubjectId, new AnalyticsQueryRequest());

        var position = result.Result!.Items.Should().ContainSingle().Subject;
        position.UnpricedEntryCount.Should().Be(2);
        position.UnstampableEntryCount.Should().Be(1, "only the Swap can never be stamped");
    }

    // ── Positions: today's truthful answer ────────────────────────────────────

    [Fact]
    public async Task GetPositionsAsync_NothingImplementsARateSource_ReturnsNullTotalsAndAFullUnpricedCount()
    {
        var service = ServiceReturning(
            Operation(amount: "100"),
            Operation(amount: "50"));

        var result = await service.GetPositionsAsync(SubjectId, new AnalyticsQueryRequest());

        var position = result.Result!.Items.Should().ContainSingle().Subject;
        position.RealizedValue.Should().BeNull("an unpriced leg is never summed as zero");
        position.UnrealizedValue.Should().BeNull();
        position.IsValueComplete.Should().BeFalse();
        position.PricedEntryCount.Should().Be(0);
        position.UnpricedEntryCount.Should().Be(2);
    }

    [Fact]
    public async Task GetPositionsAsync_OneUnpricedLeg_SuppressesTheFiatTotalEntirely()
    {
        var service = ServiceReturning(
            Operation(amount: "100", stamp: Stamp(minorUnits: "25000")),
            Operation(amount: "50"));

        var result = await service.GetPositionsAsync(SubjectId, new AnalyticsQueryRequest());

        var position = result.Result!.Items.Single();
        position.RealizedValue.Should().BeNull();
        position.PricedEntryCount.Should().Be(1);
        position.UnpricedEntryCount.Should().Be(1);
    }

    [Fact]
    public async Task GetPositionsAsync_EveryLegPriced_TotalsTheStampedMinorUnitsWithDirection()
    {
        var service = ServiceReturning(
            Operation(type: "Mint", status: OperationStatus.Minted, amount: "100", stamp: Stamp(minorUnits: "25000")),
            Operation(type: "Transfer", status: OperationStatus.Transferred, amount: "30", stamp: Stamp(minorUnits: "7500")));

        var result = await service.GetPositionsAsync(SubjectId, new AnalyticsQueryRequest());

        var position = result.Result!.Items.Single();
        position.IsValueComplete.Should().BeTrue();
        position.IsQuantityComplete.Should().BeTrue();
        position.RealizedValue!.CurrencyCode.Should().Be("USD");
        position.RealizedValue.MinorUnits.Should().Be("17500");
    }

    [Fact]
    public async Task GetPositionsAsync_StampsInDifferentCurrencies_RefusesToTotal()
    {
        var service = ServiceReturning(
            Operation(amount: "100", stamp: Stamp(minorUnits: "25000")),
            Operation(amount: "50", stamp: Stamp(minorUnits: "9000", currency: "EUR")));

        var result = await service.GetPositionsAsync(SubjectId, new AnalyticsQueryRequest());

        result.Result!.Items.Single().RealizedValue.Should().BeNull();
    }

    // ── Positions: quantity completeness ──────────────────────────────────────

    [Fact]
    public async Task GetPositionsAsync_MintAndTransfer_NetsToASignedQuantity()
    {
        var service = ServiceReturning(
            Operation(type: "Mint", status: OperationStatus.Minted, amount: "100"),
            Operation(type: "Transfer", status: OperationStatus.Transferred, amount: "30"));

        var result = await service.GetPositionsAsync(SubjectId, new AnalyticsQueryRequest());

        var position = result.Result!.Items.Single();
        position.Quantity.Should().Be("70");
        position.IsQuantityComplete.Should().BeTrue();
    }

    [Fact]
    public async Task GetPositionsAsync_OutflowExceedingInflow_KeepsTheNegativeSign()
    {
        var service = ServiceReturning(
            Operation(type: "Burn", status: OperationStatus.Burned, amount: "40"));

        var result = await service.GetPositionsAsync(SubjectId, new AnalyticsQueryRequest());

        result.Result!.Items.Single().Quantity.Should().Be("-40");
    }

    [Fact]
    public async Task GetPositionsAsync_SwapLeg_IsExcludedFromQuantityAndMarksItIncomplete()
    {
        var service = ServiceReturning(
            Operation(type: "Mint", status: OperationStatus.Minted, amount: "100"),
            Operation(type: "Swap", status: OperationStatus.Swapped, amount: "999"));

        var result = await service.GetPositionsAsync(SubjectId, new AnalyticsQueryRequest());

        var position = result.Result!.Items.Single();
        position.Quantity.Should().Be("100", "a Swap moves two assets, so it has no direction to fold");
        position.UnclassifiedEntryCount.Should().Be(1);
        position.IsQuantityComplete.Should().BeFalse();
    }

    [Fact]
    public async Task GetPositionsAsync_UnsettledLeg_IsExcludedFromQuantityAndMarksItIncomplete()
    {
        var service = ServiceReturning(
            Operation(type: "Mint", status: OperationStatus.Minted, amount: "100"),
            Operation(type: "Mint", status: OperationStatus.PendingConfirmation, amount: "500"));

        var result = await service.GetPositionsAsync(SubjectId, new AnalyticsQueryRequest());

        var position = result.Result!.Items.Single();
        position.Quantity.Should().Be("100");
        position.UnsettledEntryCount.Should().Be(1);
        position.IsQuantityComplete.Should().BeFalse();
    }

    [Fact]
    public async Task GetPositionsAsync_DifferentChainsAndAssets_AreSeparatePositions()
    {
        var service = ServiceReturning(
            Operation(chainType: "Algorand", assetId: "1001"),
            Operation(chainType: "Algorand", assetId: "2002"),
            Operation(chainType: "Solana", assetId: "1001"));

        var result = await service.GetPositionsAsync(SubjectId, new AnalyticsQueryRequest());

        result.Result!.Items.Should().HaveCount(3);
        result.Result.ScannedEntryCount.Should().Be(3);
        result.Result.IsComplete.Should().BeTrue();
    }

    // ── Paging and the self-scoped window ─────────────────────────────────────

    [Fact]
    public async Task GetTransactionsAsync_AlwaysQueriesTheAuthenticatedSubject()
    {
        var store = new Mock<IBlockchainOperationStore>(MockBehavior.Strict);
        BlockchainOperationQuery? captured = null;
        store.Setup(s => s.QueryAsync(It.IsAny<BlockchainOperationQuery>(), It.IsAny<CancellationToken>()))
            .Callback((BlockchainOperationQuery q, CancellationToken _) => captured = q)
            .ReturnsAsync(Page());

        await new AnalyticsReadService(store.Object)
            .GetTransactionsAsync(SubjectId, new AnalyticsQueryRequest { Take = 10 });

        captured!.UserId.Should().Be(SubjectId);
        captured.Take.Should().Be(10);
    }

    [Fact]
    public async Task GetTransactionsAsync_OversizedTake_IsClampedToTheServiceMaximum()
    {
        var store = new Mock<IBlockchainOperationStore>(MockBehavior.Strict);
        BlockchainOperationQuery? captured = null;
        store.Setup(s => s.QueryAsync(It.IsAny<BlockchainOperationQuery>(), It.IsAny<CancellationToken>()))
            .Callback((BlockchainOperationQuery q, CancellationToken _) => captured = q)
            .ReturnsAsync(Page());

        var result = await new AnalyticsReadService(store.Object)
            .GetTransactionsAsync(SubjectId, new AnalyticsQueryRequest { Take = 100_000 });

        captured!.Take.Should().Be(AnalyticsReadService.MaxPageSize);
        result.Result!.Take.Should().Be(AnalyticsReadService.MaxPageSize);
    }

    [Fact]
    public async Task GetTransactionsAsync_FullPage_AdvertisesMore()
    {
        var operations = Enumerable.Range(0, 3).Select(_ => (IBlockchainOperation)Operation()).ToList();
        var store = StoreReturning(operations);

        var result = await new AnalyticsReadService(store.Object)
            .GetTransactionsAsync(SubjectId, new AnalyticsQueryRequest { Take = 3 });

        result.Result!.HasMore.Should().BeTrue();
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(null, 0)]
    [InlineData(null, -5)]
    public async Task GetTransactionsAsync_MalformedPage_IsRejectedWithoutTouchingTheStore(int? skip, int? take)
    {
        var store = new Mock<IBlockchainOperationStore>(MockBehavior.Strict);

        var result = await new AnalyticsReadService(store.Object)
            .GetTransactionsAsync(SubjectId, new AnalyticsQueryRequest { Skip = skip, Take = take });

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(AzoaErrorCodes.InvalidRequest);
        store.Verify(
            s => s.QueryAsync(It.IsAny<BlockchainOperationQuery>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetTransactionsAsync_InvertedWindow_IsRejected()
    {
        var store = new Mock<IBlockchainOperationStore>(MockBehavior.Strict);

        var result = await new AnalyticsReadService(store.Object).GetTransactionsAsync(
            SubjectId,
            new AnalyticsQueryRequest
            {
                From = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
                To = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(AzoaErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task GetPositionsAsync_WindowSpanningSeveralPages_KeepsWalkingFromTheRightOffset()
    {
        var firstPage = Enumerable.Range(0, AnalyticsReadService.MaxPageSize)
            .Select(_ => (IBlockchainOperation)Operation(amount: "1"))
            .ToList();
        var secondPage = new List<IBlockchainOperation> { Operation(amount: "1") };

        var offsets = new List<int>();
        var store = new Mock<IBlockchainOperationStore>(MockBehavior.Strict);
        store.Setup(s => s.QueryAsync(It.IsAny<BlockchainOperationQuery>(), It.IsAny<CancellationToken>()))
            .Callback((BlockchainOperationQuery q, CancellationToken _) => offsets.Add(q.Skip))
            .ReturnsAsync(() => offsets.Count == 1 ? Page(firstPage) : Page(secondPage));

        var result = await new AnalyticsReadService(store.Object)
            .GetPositionsAsync(SubjectId, new AnalyticsQueryRequest());

        offsets.Should().Equal(0, AnalyticsReadService.MaxPageSize);
        result.Result!.ScannedEntryCount.Should().Be(AnalyticsReadService.MaxPageSize + 1);
        result.Result.IsComplete.Should().BeTrue();
        result.Result.Items.Single().Quantity.Should().Be((AnalyticsReadService.MaxPageSize + 1).ToString());
    }

    [Fact]
    public async Task GetTransactionsAsync_StoreFailure_SurfacesTheDependencyCodeAndNoRows()
    {
        var store = new Mock<IBlockchainOperationStore>(MockBehavior.Strict);
        store.Setup(s => s.QueryAsync(It.IsAny<BlockchainOperationQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IReadOnlyList<IBlockchainOperation>>.FailureWithCode(
                "down", AzoaErrorCodes.DependencyUnavailable));

        var result = await new AnalyticsReadService(store.Object)
            .GetTransactionsAsync(SubjectId, new AnalyticsQueryRequest());

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(AzoaErrorCodes.DependencyUnavailable);
        result.Result.Should().BeNull();
    }

    // ── Field projection ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetTransactionsAsync_ProjectsTheChainFactsTheLogActuallyCarries()
    {
        var operation = Operation(type: "Transfer", status: OperationStatus.Transferred, amount: "42");
        operation.Parameters["TxHash"] = "0xdeadbeef";
        operation.Parameters["NodeFeeAmount"] = "7";
        operation.RecipientAddress = "RECIPIENT";
        operation.CompletedDate = Settled;

        var service = ServiceReturning(operation);

        var entry = (await service.GetTransactionsAsync(SubjectId, new AnalyticsQueryRequest()))
            .Result!.Items.Single();

        entry.OperationId.Should().Be(operation.Id);
        entry.OperationType.Should().Be("Transfer");
        entry.Status.Should().Be(OperationStatus.Transferred);
        entry.ChainType.Should().Be("Algorand");
        entry.AssetId.Should().Be("1001");
        entry.AssetType.Should().Be("Token");
        entry.Amount.Should().Be("42");
        entry.CounterpartyAddress.Should().Be("RECIPIENT");
        entry.TxHash.Should().Be("0xdeadbeef");
        entry.Fee.Should().Be("7");
        entry.Timestamp.Should().Be(new DateTimeOffset(Settled));
        entry.SettledAt.Should().Be(new DateTimeOffset(Settled));
    }

    [Fact]
    public async Task GetTransactionsAsync_WeiScaleAmount_SurvivesAsADecimalString()
    {
        // The wire field is a string precisely so a 78-digit base-unit count does not become
        // a lossy JSON number on the way to a tax spreadsheet.
        const string Wei = "123456789012345678901234567890";
        var service = ServiceReturning(Operation(amount: Wei));

        var entry = (await service.GetTransactionsAsync(SubjectId, new AnalyticsQueryRequest()))
            .Result!.Items.Single();

        entry.Amount.Should().Be(Wei);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static AnalyticsReadService ServiceReturning(params BlockchainOperation[] operations) =>
        new(StoreReturning(operations.Cast<IBlockchainOperation>().ToList()).Object);

    private static Mock<IBlockchainOperationStore> StoreReturning(IReadOnlyList<IBlockchainOperation> operations)
    {
        var store = new Mock<IBlockchainOperationStore>(MockBehavior.Strict);
        store.Setup(s => s.QueryAsync(It.IsAny<BlockchainOperationQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(operations));
        return store;
    }

    private static AZOAResult<IReadOnlyList<IBlockchainOperation>> Page(
        IReadOnlyList<IBlockchainOperation>? operations = null) =>
        AZOAResult<IReadOnlyList<IBlockchainOperation>>.Success(operations ?? []);

    /// <summary>
    /// One operation row. A null <paramref name="amount"/> means the log records NO quantity
    /// at all — neither the typed field nor the parameter — which is the state the surface
    /// must report as unknown rather than as zero.
    /// </summary>
    private static BlockchainOperation Operation(
        string type = "Mint",
        string status = OperationStatus.Minted,
        string? amount = "100",
        string? chainType = "Algorand",
        string? assetId = "1001",
        ValueStamp? stamp = null)
    {
        var operation = new BlockchainOperation
        {
            Id = Guid.NewGuid(),
            UserId = SubjectId,
            OperationType = type,
            Status = status,
            Amount = amount is null
                ? RailAmount.Zero
                : RailAmount.OfChainAsset(BigInteger.Parse(amount)),
            AssetType = "Token",
            CreatedDate = Settled,
            ValueStamp = stamp,
        };

        if (amount is not null)
            operation.Parameters["Amount"] = amount;
        if (chainType is not null)
            operation.Parameters["ChainType"] = chainType;
        if (assetId is not null)
            operation.Parameters["AssetId"] = assetId;

        return operation;
    }

    private static ValueStamp Stamp(
        string rate = "250",
        string minorUnits = "25000",
        string currency = "USD") =>
        ValueStamp.FromPersisted(
            currency,
            minorUnits,
            rate,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "test-source")!;
}
