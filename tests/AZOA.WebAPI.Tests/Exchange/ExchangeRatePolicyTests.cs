// SPDX-License-Identifier: UNLICENSED
// W5.1 — the rate policy: which declarations may price a real value move, and the null check
// that keeps the reverse direction shut until W8.2 lands a liquidity policy.

using System.Numerics;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Xunit;

namespace AZOA.WebAPI.Tests.Exchange;

public class ExchangeRatePolicyTests
{
    private const string Chain = "Algorand";
    private const string PlatformAsset = "1001";
    private const string ProjectAsset = "2002";

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    // ── The reverse direction ─────────────────────────────────────────────────

    [Fact]
    public async Task Selling_is_refused_while_no_liquidity_policy_is_registered()
    {
        var policy = new TreasuryRateExchangePolicy(new ExchangeOptions());

        var decision = await policy.EvaluateAsync(Evaluation(direction: ExchangeDirection.Sell));

        decision.IsError.Should().BeTrue(
            "a sell with no liquidity behind it is a promise the node cannot keep");
        decision.Message.Should().Be(TreasuryRateExchangePolicy.SellUnavailableMessage);
        decision.Code.Should().Be(AzoaErrorCodes.OperationNotAllowed);
    }

    [Fact]
    public async Task Selling_is_admitted_once_a_liquidity_policy_allows_it()
    {
        var policy = new TreasuryRateExchangePolicy(
            new ExchangeOptions(), new StubLiquidityPolicy(allowed: true));

        var decision = await policy.EvaluateAsync(Evaluation(direction: ExchangeDirection.Sell));

        decision.IsError.Should().BeFalse(decision.Message);
    }

    [Fact]
    public async Task A_liquidity_policy_that_refuses_stops_the_sell()
    {
        var policy = new TreasuryRateExchangePolicy(
            new ExchangeOptions(), new StubLiquidityPolicy(allowed: false));

        var decision = await policy.EvaluateAsync(Evaluation(direction: ExchangeDirection.Sell));

        decision.IsError.Should().BeTrue();
    }

    // ── Which declarations may price a move ───────────────────────────────────

    [Fact]
    public async Task A_buy_at_a_fresh_declared_rate_is_admitted_and_stamped_with_the_policy_version()
    {
        var policy = new TreasuryRateExchangePolicy(new ExchangeOptions());

        var decision = await policy.EvaluateAsync(Evaluation());

        decision.IsError.Should().BeFalse(decision.Message);
        decision.Result!.PolicyVersion.Should().Be(TreasuryRateExchangePolicy.Version);
        decision.Result.DeclaredRate!.AssetId.Should().Be(ProjectAsset);
    }

    [Fact]
    public async Task An_exchange_with_no_declared_rate_is_refused_by_default()
    {
        var policy = new TreasuryRateExchangePolicy(new ExchangeOptions());

        var decision = await policy.EvaluateAsync(EvaluationWithoutRate());

        decision.IsError.Should().BeTrue("a value move nobody priced cannot be audited");
    }

    [Fact]
    public async Task A_rate_older_than_the_configured_window_is_refused_rather_than_reused()
    {
        var policy = new TreasuryRateExchangePolicy(
            new ExchangeOptions { MaxDeclaredRateAge = TimeSpan.FromHours(1) });

        var decision = await policy.EvaluateAsync(
            Evaluation(rate: Rate(declaredAt: Now.AddHours(-2))));

        decision.IsError.Should().BeTrue("stale presented as current is a fabricated price");
        decision.Message.Should().Contain("older");
    }

    [Fact]
    public async Task A_rate_dated_after_the_exchange_it_prices_is_refused()
    {
        var policy = new TreasuryRateExchangePolicy(new ExchangeOptions());

        var decision = await policy.EvaluateAsync(
            Evaluation(rate: Rate(declaredAt: Now.AddHours(1))));

        decision.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task A_rate_for_a_different_asset_is_refused_rather_than_used_as_a_fallback()
    {
        var policy = new TreasuryRateExchangePolicy(new ExchangeOptions());

        var decision = await policy.EvaluateAsync(
            Evaluation(rate: Rate(assetId: "9999")));

        decision.IsError.Should().BeTrue();
        decision.Message.Should().Contain("does not price the units");
    }

    [Fact]
    public async Task A_rate_declared_for_another_rail_is_refused()
    {
        var policy = new TreasuryRateExchangePolicy(new ExchangeOptions());

        var decision = await policy.EvaluateAsync(
            Evaluation(rate: Rate(chainType: "Solana")));

        decision.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task A_rate_of_zero_cannot_price_a_sponsorship()
    {
        var policy = new TreasuryRateExchangePolicy(new ExchangeOptions());

        var decision = await policy.EvaluateAsync(Evaluation(rate: Rate(minorUnits: 0m)));

        decision.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task A_non_positive_amount_on_either_side_is_refused()
    {
        var policy = new TreasuryRateExchangePolicy(new ExchangeOptions());

        var decision = await policy.EvaluateAsync(
            Evaluation() with { ProjectUnitAmount = BigInteger.Zero });

        decision.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task An_exchange_of_one_asset_for_itself_is_refused()
    {
        var policy = new TreasuryRateExchangePolicy(new ExchangeOptions());

        var decision = await policy.EvaluateAsync(
            Evaluation() with { ProjectAssetId = PlatformAsset });

        decision.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task A_node_configured_with_no_rate_window_prices_nothing()
    {
        var policy = new TreasuryRateExchangePolicy(
            new ExchangeOptions { MaxDeclaredRateAge = TimeSpan.Zero });

        var decision = await policy.EvaluateAsync(Evaluation());

        decision.IsError.Should().BeTrue("a non-positive window is the fail-closed reading");
    }

    // ── Binding the wire shape ────────────────────────────────────────────────

    [Fact]
    public void A_whole_unit_price_binds_to_the_per_base_unit_rate_the_stamp_needs()
    {
        var bound = DeclaredRateBinding.TryBind(
            new DeclaredExchangeRate
            {
                MajorUnitsPerWholeUnit = "0.01",
                AssetDecimals = 2,
                CurrencyMinorUnitDigits = 2,
                DeclaredAt = Now,
                Source = "ardanova:treasury-rate",
                Version = "7",
            },
            Chain, ProjectAsset, out var rate, out var error);

        bound.Should().BeTrue(error);
        rate!.MinorUnitsPerBaseUnit.Should().Be(
            0.01m, "one whole unit at two decimals is 100 base units, so a cent per unit is 0.01 per base unit");
        rate.Version.Should().Be("7");
    }

    [Fact]
    public void A_price_that_cannot_be_represented_exactly_is_refused_rather_than_rounded()
    {
        var bound = DeclaredRateBinding.TryBind(
            new DeclaredExchangeRate
            {
                MajorUnitsPerWholeUnit = "0.0000000000001",
                AssetDecimals = 18,
                CurrencyMinorUnitDigits = 2,
                DeclaredAt = Now,
                Source = "ardanova:treasury-rate",
            },
            Chain, ProjectAsset, out _, out var error);

        bound.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_declaration_carrying_both_numeric_forms_is_refused_as_ambiguous()
    {
        var bound = DeclaredRateBinding.TryBind(
            new DeclaredExchangeRate
            {
                MajorUnitsPerWholeUnit = "0.01",
                MinorUnitsPerBaseUnit = "0.01",
                AssetDecimals = 2,
                DeclaredAt = Now,
                Source = "ardanova:treasury-rate",
            },
            Chain, ProjectAsset, out _, out var error);

        bound.Should().BeFalse();
        error.Should().Contain("exactly one");
    }

    [Fact]
    public void A_declaration_with_no_source_is_refused()
    {
        var bound = DeclaredRateBinding.TryBind(
            new DeclaredExchangeRate { MinorUnitsPerBaseUnit = "0.01", DeclaredAt = Now },
            Chain, ProjectAsset, out _, out var error);

        bound.Should().BeFalse();
        error.Should().Contain("where it came from");
    }

    [Fact]
    public void No_declaration_at_all_binds_to_nothing_without_erroring()
    {
        DeclaredRateBinding.TryBind(null, Chain, ProjectAsset, out var rate, out var error)
            .Should().BeTrue(error);
        rate.Should().BeNull();
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static ExchangeRateEvaluation Evaluation(
        ExchangeDirection direction = ExchangeDirection.Buy,
        TenantDeclaredRate? rate = null)
        => new(
            Chain,
            direction,
            ProjectAsset,
            PlatformAsset,
            new BigInteger(500),
            new BigInteger(250),
            rate ?? Rate(),
            Now);

    /// <summary>
    /// An evaluation carrying NO declared rate. This needs its own fixture because
    /// <see cref="Evaluation"/> substitutes a default for a null argument, so passing
    /// <c>rate: null</c> would quietly price the exchange and test the opposite case.
    /// </summary>
    private static ExchangeRateEvaluation EvaluationWithoutRate()
        => Evaluation() with { DeclaredRate = null };

    private static TenantDeclaredRate Rate(
        string chainType = Chain,
        string assetId = ProjectAsset,
        decimal minorUnits = 0.05m,
        DateTimeOffset? declaredAt = null)
        => new(
            chainType,
            assetId,
            "USD",
            minorUnits,
            declaredAt ?? Now.AddMinutes(-5),
            "ardanova:treasury-rate",
            "1");

    private sealed class StubLiquidityPolicy : IExchangeLiquidityPolicy
    {
        private readonly bool _allowed;

        public StubLiquidityPolicy(bool allowed) => _allowed = allowed;

        public Task<AZOAResult<bool>> EnsureSellAllowedAsync(
            ExchangeRateEvaluation evaluation, CancellationToken ct = default)
            => Task.FromResult(_allowed
                ? AZOAResult<bool>.Success(true)
                : AZOAResult<bool>.Failure("The buy-back queue is full right now."));
    }
}
