// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using System.Reflection;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// D13 (amount widening) and D16 (USD value at transaction time) on the operation record.
///
/// <para>The store's domain⇔POCO mapping is exercised through its private
/// <c>ToPoco</c>/<c>ToDomain</c> statics. The public write path needs a live
/// <c>SurrealResponse</c>, which cannot be constructed in-process; going through the
/// private pair is what makes "operation record → store column → read back" assertable at
/// all, and the column is the thing under test.</para>
/// </summary>
public sealed class WideAmountAndValueStampTests
{
    // 1000 ETH in wei — ~54× above ulong.MaxValue, and an entirely ordinary holding.
    private const string ThousandEtherInWei = "1000000000000000000000";
    private const string TwoToTheSixtyFour = "18446744073709551616";
    private const string MaxUint256 =
        "115792089237316195423570985008687907853269984665640564039457584007913129639935";

    // ── D13: the full Mint path ────────────────────────────────────────────────

    /// <summary>
    /// interface → manager → operation record → store column → read back, with an amount
    /// the retired <c>ulong</c> surface could not express. Every hop must carry the exact
    /// base-unit count; a single narrowing anywhere shows up here.
    /// </summary>
    [Theory]
    [InlineData(TwoToTheSixtyFour)]
    [InlineData(ThousandEtherInWei)]
    [InlineData(MaxUint256)]
    public async Task Mint_AmountAboveUlongMax_SurvivesTheWholeRoundTrip(string baseUnits)
    {
        var expected = BigInteger.Parse(baseUnits);
        RailAmount? seenByProvider = null;

        var provider = new Mock<IBlockchainProvider>();
        provider.Setup(p => p.ChainType).Returns("Algorand");
        provider.Setup(p => p.MintAsync(
                    It.IsAny<string>(), It.IsAny<RailAmount>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                .Callback<string, RailAmount, string, string, SigningContext?, CancellationToken>(
                    (_, amount, _, _, _, _) => seenByProvider = amount)
                .ReturnsAsync(new AZOAResult<string> { Result = "algo_tx_wide" });

        IBlockchainOperation? persisted = null;
        var manager = BuildManager(provider, capture: op => persisted = op);

        var result = await manager.ExecuteAsync(MintOp(RailAmount.OfChainAsset(expected)));

        result.IsError.Should().BeFalse(result.Message);

        // 1. interface → provider
        seenByProvider.Should().NotBeNull();
        seenByProvider!.Value.BaseUnits.Should().Be(expected, "the manager must not narrow the mint amount");

        // 2. the operation record handed to the store
        var record = persisted.Should().BeAssignableTo<IMintOperation>().Subject;
        record.Amount.BaseUnits.Should().Be(expected);

        // 3. record → column → record
        var poco = ToPoco(record);
        poco.Amount.Should().Be(baseUnits, "the column is a decimal base-unit STRING, not a number");
        ToDomain(poco).Amount.BaseUnits.Should().Be(expected);
    }

    [Fact]
    public void OperationAmountColumn_IsAStringSoNoDatabaseTypeCapsIt()
    {
        typeof(OperationLog).GetProperty(nameof(OperationLog.Amount))!.PropertyType
            .Should().Be<string>(
                "a numeric column would re-impose the 64-bit ceiling D13 removed — an "
                + "ordinary wei-denominated amount does not fit in i64");
    }

    /// <summary>
    /// A deployed database is built from the IMMUTABLE compatibility baseline plus forward
    /// migrations, not from the regenerated schema
    /// (<c>Persistence/SurrealDb/CompatibilityBaselines/README.md</c>). The baseline still
    /// declares <c>amount</c> as <c>option&lt;int&gt;</c>, so regenerating the golden alone
    /// would leave every deployment with the old column and reject both the string amount
    /// and the D16 columns at the SCHEMAFULL boundary. This pins the forward migration that
    /// closes that gap.
    /// </summary>
    [Fact]
    public void ColumnChanges_AreCarriedByAForwardMigration_NotOnlyTheRegeneratedGolden()
    {
        var root = FindRepositoryRoot();

        File.ReadAllText(Path.Combine(
                root, "Persistence", "SurrealDb", "CompatibilityBaselines", "operation_log.surql"))
            .Should().Contain("amount ON TABLE operation_log TYPE option<int>",
                "the shipped baseline is immutable — it is the reason a migration is required");

        var migration = File.ReadAllText(Path.Combine(
            root, "Persistence", "SurrealDb", "Migrations",
            "20260821_120000__widen_operation_amount_and_stamp_value.surql"));

        migration.Should()
            .Contain("DEFINE FIELD OVERWRITE amount ON TABLE operation_log TYPE option<int | string>",
                "a stored i64 must stay readable while it is being rewritten")
            .And.Contain("SET amount = <string> amount",
                "existing numeric amounts must be rewritten into the canonical string form")
            .And.Contain("DEFINE FIELD OVERWRITE amount ON TABLE operation_log TYPE option<string>")
            .And.Contain("value_currency_code")
            .And.Contain("value_minor_units")
            .And.Contain("value_rate")
            .And.Contain("value_rate_as_of")
            .And.Contain("value_rate_source");

        // The migration must land the same shape the POCO generates, or the deploy path and
        // the desired-schema golden describe two different tables.
        var generated = File.ReadAllText(Path.Combine(
            root, "Persistence", "SurrealDb", "Generated", "Schemas", "operation_log.surql"));
        foreach (var column in new[]
                 {
                     "value_currency_code", "value_minor_units", "value_rate",
                     "value_rate_as_of", "value_rate_source",
                 })
        {
            generated.Should().Contain($"{column} ON TABLE operation_log");
        }

        generated.Should().Contain("amount ON TABLE operation_log TYPE option<string>");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "azoa.sln")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }

    // ── D16: the value stamp ───────────────────────────────────────────────────

    /// <summary>
    /// A manager built with NO <see cref="IValueRateSource"/> — which is what this test does,
    /// not what the node does: <c>TenantDeclaredValueRateSource</c> implements it and
    /// <c>AnalyticsRegistration.AddValueRateSources</c> registers it. With no source the
    /// operation is written with NO stamp — every stamp column null — and that state is
    /// what "never priced" means on the row.
    /// </summary>
    [Fact]
    public async Task Operation_WithNoRateSource_RecordsAnExplicitlyAbsentRate()
    {
        IBlockchainOperation? persisted = null;
        var manager = BuildManager(MintingProvider(), capture: op => persisted = op);

        await manager.ExecuteAsync(MintOp(RailAmount.OfChainAsset(5)));

        persisted!.ValueStamp.Should().BeNull("no rate source can price this operation");

        var poco = ToPoco(persisted);
        poco.ValueCurrencyCode.Should().BeNull();
        poco.ValueMinorUnits.Should().BeNull();
        poco.ValueRate.Should().BeNull();
        poco.ValueRateAsOf.Should().BeNull();
        poco.ValueRateSource.Should().BeNull();

        ToDomain(poco).ValueStamp.Should().BeNull("an unstamped row must read back unstamped");
    }

    /// <summary>
    /// The distinction the whole design turns on: a source that genuinely quotes zero
    /// produces a REAL stamp whose rate is <c>"0"</c>, and that row is not the same row as
    /// one nothing could price. Both persist and both read back as themselves.
    /// </summary>
    [Fact]
    public async Task Operation_WithAZeroRateQuote_IsDistinguishableFromAnAbsentRate()
    {
        var quotedAt = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        IBlockchainOperation? priced = null;
        var pricedManager = BuildManager(
            MintingProvider(),
            capture: op => priced = op,
            rateSource: new StubRateSource(0m, quotedAt, "stub-oracle"));

        await pricedManager.ExecuteAsync(MintOp(RailAmount.OfChainAsset(5)));

        IBlockchainOperation? unpriced = null;
        var unpricedManager = BuildManager(MintingProvider(), capture: op => unpriced = op);
        await unpricedManager.ExecuteAsync(MintOp(RailAmount.OfChainAsset(5)));

        // Stamped-zero: a real answer from a real source.
        priced!.ValueStamp.Should().NotBeNull();
        priced.ValueStamp!.Rate.Should().Be("0");
        priced.ValueStamp.MinorUnits.Should().Be("0");
        priced.ValueStamp.CurrencyCode.Should().Be("USD");
        priced.ValueStamp.RateAsOf.Should().Be(quotedAt);
        priced.ValueStamp.RateSource.Should().Be("stub-oracle");

        // Unstamped: no answer at all.
        unpriced!.ValueStamp.Should().BeNull();

        // And the two are still different rows after a persistence round trip.
        var pricedRow = ToPoco(priced);
        var unpricedRow = ToPoco(unpriced);
        pricedRow.ValueRate.Should().Be("0");
        unpricedRow.ValueRate.Should().BeNull();
        ToDomain(pricedRow).ValueStamp!.Rate.Should().Be("0");
        ToDomain(unpricedRow).ValueStamp.Should().BeNull();
    }

    /// <summary>
    /// The rate and its timestamp are stored alongside the figure, not just the figure:
    /// that is what keeps the number auditable and re-derivable later.
    /// </summary>
    [Fact]
    public async Task Operation_WithARateQuote_StampsTheRateAndItsTimestamp()
    {
        // 250 USD cents per base unit, applied to 4 base units ⇒ 1000 cents ($10.00).
        var quotedAt = new DateTimeOffset(2026, 8, 21, 9, 30, 0, TimeSpan.Zero);
        IBlockchainOperation? persisted = null;
        var manager = BuildManager(
            MintingProvider(),
            capture: op => persisted = op,
            rateSource: new StubRateSource(250m, quotedAt, "coingecko-stub"));

        await manager.ExecuteAsync(MintOp(RailAmount.OfChainAsset(4)));

        persisted!.ValueStamp.Should().NotBeNull();
        var stamp = persisted.ValueStamp!;
        stamp.MinorUnits.Should().Be("1000");
        stamp.Rate.Should().Be("250");
        stamp.RateAsOf.Should().Be(quotedAt);

        var round = ToDomain(ToPoco(persisted)).ValueStamp!;
        round.Should().Be(stamp, "every component of the stamp survives the column round trip");
    }

    [Fact]
    public async Task FailedOperation_IsNotStamped()
    {
        var provider = new Mock<IBlockchainProvider>();
        provider.Setup(p => p.ChainType).Returns("Algorand");
        provider.Setup(p => p.MintAsync(
                    It.IsAny<string>(), It.IsAny<RailAmount>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<string>.Failure("chain refused"));

        IBlockchainOperation? persisted = null;
        var manager = BuildManager(
            provider,
            capture: op => persisted = op,
            rateSource: new StubRateSource(250m, DateTimeOffset.UtcNow, "stub"));

        await manager.ExecuteAsync(MintOp(RailAmount.OfChainAsset(4)));

        persisted!.Status.Should().Be(OperationStatus.Failed);
        persisted.ValueStamp.Should().BeNull("nothing moved, so there is no value to stamp");
    }

    /// <summary>
    /// A rate source that throws must not turn a settled chain effect into a failed
    /// operation, and must not leave a guessed figure behind either.
    /// </summary>
    [Fact]
    public async Task Operation_WhenTheRateSourceThrows_SettlesUnpricedRatherThanFailing()
    {
        IBlockchainOperation? persisted = null;
        var manager = BuildManager(
            MintingProvider(),
            capture: op => persisted = op,
            rateSource: new ThrowingRateSource());

        var result = await manager.ExecuteAsync(MintOp(RailAmount.OfChainAsset(4)));

        result.IsError.Should().BeFalse(result.Message);
        persisted!.Status.Should().Be(OperationStatus.Minted);
        persisted.ValueStamp.Should().BeNull();
    }

    /// <summary>
    /// The stamp arithmetic runs entirely in <see cref="BigInteger"/>. Converting the
    /// amount to <see cref="decimal"/> first would overflow above ~7.9e28, which would put
    /// a magnitude ceiling back on a financial figure that its own amount type no longer has.
    /// </summary>
    [Theory]
    [InlineData("1", "1", "1")]
    [InlineData("4", "250", "1000")]
    [InlineData("3", "0.5", "2")]                                  // 1.5 ⇒ 2, half-up
    [InlineData("1", "0.4", "0")]
    [InlineData(MaxUint256, "1", MaxUint256)]
    [InlineData(ThousandEtherInWei, "0.0000000000000003", "300000")]
    public void ValueStamp_ConvertsExactlyAtAnyMagnitude(string baseUnits, string rate, string expectedMinorUnits)
    {
        var stamp = ValueStamp.From(
            RailAmount.OfChainAsset(BigInteger.Parse(baseUnits, CultureInfo.InvariantCulture)),
            new ValueRateQuote(
                ValueDimension.Usd,
                decimal.Parse(rate, CultureInfo.InvariantCulture),
                DateTimeOffset.UnixEpoch,
                "test"));

        stamp.MinorUnits.Should().Be(expectedMinorUnits);
    }

    [Fact]
    public void ValueRateQuote_RefusesANegativeRateAndANonFiatTarget()
    {
        var negative = () => new ValueRateQuote(ValueDimension.Usd, -1m, DateTimeOffset.UnixEpoch, "test");
        negative.Should().Throw<ArgumentOutOfRangeException>();

        var nonFiat = () => new ValueRateQuote(
            ValueDimension.NativeChainAsset, 1m, DateTimeOffset.UnixEpoch, "test");
        nonFiat.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task DefaultRateSource_NeverQuotes()
    {
        var quote = await UnpricedValueRateSource.Instance.TryQuoteAsync(
            ValueDimension.NativeChainAsset, "Algorand", DateTimeOffset.UtcNow);

        quote.Should().BeNull("this codebase has no price oracle and must not pretend otherwise");
    }

    // ── Fixtures ───────────────────────────────────────────────────────────────

    private sealed class StubRateSource(decimal rate, DateTimeOffset asOf, string source) : IValueRateSource
    {
        public ValueTask<ValueRateQuote?> TryQuoteAsync(
            ValueDimension dimension, string chainType, DateTimeOffset requestedAt, CancellationToken ct = default)
            => ValueTask.FromResult<ValueRateQuote?>(
                new ValueRateQuote(ValueDimension.Usd, rate, asOf, source));
    }

    private sealed class ThrowingRateSource : IValueRateSource
    {
        public ValueTask<ValueRateQuote?> TryQuoteAsync(
            ValueDimension dimension, string chainType, DateTimeOffset requestedAt, CancellationToken ct = default)
            => throw new InvalidOperationException("oracle unreachable");
    }

    private static Mock<IBlockchainProvider> MintingProvider()
    {
        var provider = new Mock<IBlockchainProvider>();
        provider.Setup(p => p.ChainType).Returns("Algorand");
        provider.Setup(p => p.MintAsync(
                    It.IsAny<string>(), It.IsAny<RailAmount>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AZOAResult<string> { Result = "algo_tx" });
        return provider;
    }

    private static BlockchainOperationManager BuildManager(
        Mock<IBlockchainProvider> provider,
        Action<IBlockchainOperation> capture,
        IValueRateSource? rateSource = null)
    {
        var store = new Mock<IBlockchainOperationStore>();
        store.Setup(s => s.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((IBlockchainOperation op, CancellationToken _) =>
             {
                 capture(op);
                 return new AZOAResult<IBlockchainOperation> { Result = op };
             });

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultChain"] = "Algorand",
                ["Blockchain:DefaultNetwork"] = "Devnet",
                ["Blockchain:Chains:0:ChainType"] = "Algorand",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
            })
            .Build();
        var factory = new BlockchainProviderFactory(
            [new BlockchainProviderRegistration("Algorand", () => provider.Object)],
            config);

        return new BlockchainOperationManager(
            store.Object, factory, new FakeIdempotencyStore(), rateSource, valueAccess: new AZOA.WebAPI.Services.Kyc.ValueAccessService(new ApprovedRealValueKycGate()));
    }

    private static BlockchainOperation MintOp(RailAmount amount) => new()
    {
        OperationType = "Mint",
        TokenUri = "ipfs://wide",
        AssetType = "FT",
        Amount = amount,
        Parameters = new Dictionary<string, string>
        {
            ["ChainType"] = "Algorand",
            ["WalletAddress"] = "ALGOTESTADDRESS",
        },
    };

    // ── Access to the store's domain ⇔ POCO mapping ─────────────────────────────

    private static readonly MethodInfo ToPocoMethod =
        typeof(SurrealBlockchainOperationStore).GetMethod(
            "ToPoco", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo ToDomainMethod =
        typeof(SurrealBlockchainOperationStore).GetMethod(
            "ToDomain", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static OperationLog ToPoco(IBlockchainOperation operation)
        => (OperationLog)ToPocoMethod.Invoke(null, [operation])!;

    private static BlockchainOperation ToDomain(OperationLog poco)
        => (BlockchainOperation)ToDomainMethod.Invoke(null, [poco])!;
}
