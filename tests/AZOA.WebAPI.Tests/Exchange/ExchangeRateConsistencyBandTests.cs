// SPDX-License-Identifier: UNLICENSED
// L4.6 / OI-79 -- the consistency band on a rate AZOA chooses to RECORD. The funds amount, the
// units amount and the declared rate all arrive on ONE consumer-supplied body, and until this
// landed nothing checked that the three could be true together before the rate was stamped on
// the receipt and on the operation row as the audit answer to "what was this worth when it
// moved". These tests pin the band's edges, its fail-closed empty case, and that a consistent
// triple still passes through untouched.

using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Xunit;

namespace AZOA.WebAPI.Tests.Exchange;

public class ExchangeRateConsistencyBandTests
{
    private const string Chain = "Algorand";
    private const string PlatformAsset = "1001";
    private const string ProjectAsset = "2002";

    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    // ── The edges, with the platform unit pinned ──────────────────────────────
    // Min == Max == 1 is an operator who has pinned what one platform base unit is worth, which
    // is the tightest the band can ever be. Every edge below reads off that pin, so it is an
    // exact integer rather than a number that happens to fall inside a wide shipped default.

    [Fact]
    public void A_triple_whose_three_numbers_agree_exactly_is_admitted()
    {
        var outcome = Band(platformAmount: 1000, projectUnitAmount: 50_000, rate: 0.02m);

        outcome.Verdict.Should().Be(RateConsistencyVerdict.Consistent);
        outcome.PricedMinorUnits.Should().Be("1000");
    }

    [Fact]
    public void One_base_unit_of_units_above_the_band_is_refused_by_name()
    {
        var outcome = Band(platformAmount: 1000, projectUnitAmount: 50_001, rate: 0.02m);

        outcome.Verdict.Should().Be(
            RateConsistencyVerdict.PricedAboveTheFunds,
            "the units side is worth more than the funds side could stand for, so one of the "
            + "three numbers on the body is wrong");
        outcome.PricedMinorUnits.Should().Be("1000.02");
        outcome.HighestAcceptableMinorUnits.Should().Be("1000");
    }

    [Fact]
    public void One_base_unit_of_units_below_the_band_is_refused_by_name()
    {
        var outcome = Band(platformAmount: 1000, projectUnitAmount: 49_999, rate: 0.02m);

        outcome.Verdict.Should().Be(RateConsistencyVerdict.PricedBelowTheFunds);
        outcome.PricedMinorUnits.Should().Be("999.98");
        outcome.LowestAcceptableMinorUnits.Should().Be("1000");
    }

    [Fact]
    public void The_dust_allowance_widens_the_band_at_both_ends_and_its_own_edge_is_admitted()
    {
        var justInsideAbove = Band(1000, 50_050, 0.02m, dust: 1m);
        var justOutsideAbove = Band(1000, 50_051, 0.02m, dust: 1m);
        var justInsideBelow = Band(1000, 49_950, 0.02m, dust: 1m);
        var justOutsideBelow = Band(1000, 49_949, 0.02m, dust: 1m);

        justInsideAbove.Verdict.Should().Be(
            RateConsistencyVerdict.Consistent, "the dust edge itself is inside the band");
        justInsideAbove.HighestAcceptableMinorUnits.Should().Be("1001");
        justOutsideAbove.Verdict.Should().Be(RateConsistencyVerdict.PricedAboveTheFunds);

        justInsideBelow.Verdict.Should().Be(RateConsistencyVerdict.Consistent);
        justInsideBelow.LowestAcceptableMinorUnits.Should().Be("999");
        justOutsideBelow.Verdict.Should().Be(RateConsistencyVerdict.PricedBelowTheFunds);
    }

    [Fact]
    public void A_dust_allowance_the_operator_blanked_widens_nothing()
    {
        // Slack only ever ADMITS more, so a negative or absent one resolves to zero rather than
        // to some allowance a caller could then lean on.
        var outcome = Band(1000, 50_001, 0.02m, dust: -5m);

        outcome.Verdict.Should().Be(RateConsistencyVerdict.PricedAboveTheFunds);
        outcome.HighestAcceptableMinorUnits.Should().Be("1000");
    }

    // ── The empty case: a blank bound is UNDECLARED, never unbounded ──────────
    // These options bind the way ReserveOptions does, so an environment variable set to "" can
    // REPLACE the C# default rather than leaving it. The empty case therefore has to resolve to
    // the narrowest setting in code, and for a band the narrowest reading is "record nothing".

    [Theory]
    [InlineData(0d, 100d)]
    [InlineData(-1d, 100d)]
    [InlineData(0.0001d, 0d)]
    [InlineData(0.0001d, -1d)]
    [InlineData(0d, 0d)]
    public void A_bound_the_operator_never_declared_refuses_rather_than_admitting_everything(
        double lowest, double highest)
    {
        var outcome = ExchangeRateConsistencyBand.Decide(
            new BigInteger(1000), new BigInteger(50_000), 0.02m,
            (decimal)lowest, (decimal)highest, dustMinorUnits: 0m);

        outcome.Verdict.Should().Be(
            RateConsistencyVerdict.BandUndeclared,
            "a blank bound that resolved to 'no limit' would be the shape a deploy produces "
            + "most easily and the one that checks nothing");
    }

    [Fact]
    public void An_inverted_band_is_treated_as_undeclared_rather_than_silently_reordered()
    {
        var outcome = ExchangeRateConsistencyBand.Decide(
            new BigInteger(1000), new BigInteger(50_000), 0.02m,
            minMinorUnitsPerPlatformBaseUnit: 10m,
            maxMinorUnitsPerPlatformBaseUnit: 1m,
            dustMinorUnits: 0m);

        outcome.Verdict.Should().Be(RateConsistencyVerdict.BandUndeclared);
    }

    // ── Magnitude: the class this band actually catches ───────────────────────

    [Fact]
    public void A_rate_supplied_per_whole_unit_where_a_per_base_unit_rate_belongs_is_caught()
    {
        // A project unit priced at 0.02 minor units per BASE unit, sent instead as 20000 per
        // base unit: the silent 10^6 the declared-rate type warns about, against the shipped
        // bounds rather than a pin.
        var outcome = ExchangeRateConsistencyBand.Decide(
            new BigInteger(500), new BigInteger(250), 20_000m,
            minMinorUnitsPerPlatformBaseUnit: 0.0001m,
            maxMinorUnitsPerPlatformBaseUnit: 100m,
            dustMinorUnits: 1m);

        outcome.Verdict.Should().Be(RateConsistencyVerdict.PricedAboveTheFunds);
    }

    [Fact]
    public void A_funds_amount_off_by_its_own_decimals_is_caught_from_the_other_side()
    {
        var outcome = ExchangeRateConsistencyBand.Decide(
            new BigInteger(500_000_000), new BigInteger(250), 0.02m,
            minMinorUnitsPerPlatformBaseUnit: 0.0001m,
            maxMinorUnitsPerPlatformBaseUnit: 100m,
            dustMinorUnits: 1m);

        outcome.Verdict.Should().Be(RateConsistencyVerdict.PricedBelowTheFunds);
    }

    [Fact]
    public void An_amount_larger_than_decimal_can_hold_is_compared_exactly_rather_than_overflowing()
    {
        // 1e40 base units is past decimal's ~7.9e28 ceiling. The band must not put a magnitude
        // limit on a financial figure that the amount type itself does not have.
        var huge = BigInteger.Pow(10, 40);

        var exact = ExchangeRateConsistencyBand.Decide(
            huge, huge, 1m, 1m, 1m, dustMinorUnits: 0m);
        var oneShort = ExchangeRateConsistencyBand.Decide(
            huge, huge - BigInteger.One, 1m, 1m, 1m, dustMinorUnits: 0m);

        exact.Verdict.Should().Be(RateConsistencyVerdict.Consistent);
        exact.PricedMinorUnits.Should().Be(huge.ToString());
        oneShort.Verdict.Should().Be(
            RateConsistencyVerdict.PricedBelowTheFunds,
            "one base unit out of 1e40 still has to register, or the band rounds real money away");
    }

    // ── The policy: where the recorded rate is actually admitted ──────────────

    [Fact]
    public async Task A_consistent_triple_still_passes_the_policy_unchanged()
    {
        var policy = new TreasuryRateExchangePolicy(PinnedOptions());

        var decision = await policy.EvaluateAsync(
            Evaluation(platformAmount: 1000, projectUnitAmount: 50_000));

        decision.IsError.Should().BeFalse(decision.Message);
        decision.Result!.DeclaredRate!.MinorUnitsPerBaseUnit.Should().Be(0.02m);
        decision.Result.PolicyVersion.Should().Be(TreasuryRateExchangePolicy.Version);
    }

    [Fact]
    public async Task A_self_contradicting_triple_is_refused_and_names_all_three_figures()
    {
        var policy = new TreasuryRateExchangePolicy(PinnedOptions());

        var decision = await policy.EvaluateAsync(
            Evaluation(platformAmount: 1000, projectUnitAmount: 5_000_000));

        decision.IsError.Should().BeTrue(
            "a rate the amounts on its own body contradict is not an audit answer");
        decision.Code.Should().Be(ConsumerSafeErrorCodes.Declined);
        decision.Message.Should().StartWith(TreasuryRateExchangePolicy.InconsistentTripleMessage);

        // By NAME: the units, the funds and what the rate makes them worth are all in the
        // sentence, so the caller can see which of the three it has to restate.
        decision.Message.Should().Contain("5000000 units");
        decision.Message.Should().Contain("funds amount of 1000");
        decision.Message.Should().Contain("100000 minor units of USD");
    }

    [Fact]
    public async Task A_triple_priced_below_the_funds_names_the_lower_end_of_the_band()
    {
        var policy = new TreasuryRateExchangePolicy(PinnedOptions());

        var decision = await policy.EvaluateAsync(
            Evaluation(platformAmount: 1000, projectUnitAmount: 1));

        decision.IsError.Should().BeTrue();
        decision.Message.Should().Contain("at least 1000");
    }

    [Fact]
    public async Task The_band_refusal_carries_no_rail_vocabulary()
    {
        var policy = new TreasuryRateExchangePolicy(PinnedOptions());

        var decision = await policy.EvaluateAsync(
            Evaluation(platformAmount: 1000, projectUnitAmount: 5_000_000));

        ConsumerSafeErrors.LeaksRailDetail(decision.Message).Should().BeFalse(
            "a sponsor who has never heard of a market reads this sentence");
    }

    [Fact]
    public async Task A_node_whose_band_was_blanked_records_no_rate_at_all()
    {
        var policy = new TreasuryRateExchangePolicy(PinnedOptions(lowest: 0m));

        var decision = await policy.EvaluateAsync(
            Evaluation(platformAmount: 1000, projectUnitAmount: 50_000));

        decision.IsError.Should().BeTrue(
            "an undeclared band admits nothing; it is not an absent check");
        decision.Message.Should().Be(TreasuryRateExchangePolicy.BandUndeclaredMessage);
        decision.Code.Should().Be(ConsumerSafeErrorCodes.Declined);
    }

    [Fact]
    public async Task The_shipped_default_band_admits_an_ordinary_sponsorship()
    {
        // The wide shipped pair exists to catch an order-of-magnitude contradiction, not to
        // price a trade: the platform unit's value is backing-derived and drifts, so an
        // ordinary exchange has to pass without an operator touching configuration.
        var policy = new TreasuryRateExchangePolicy(new ExchangeOptions());

        var decision = await policy.EvaluateAsync(
            Evaluation(platformAmount: 500, projectUnitAmount: 250));

        decision.IsError.Should().BeFalse(decision.Message);
    }

    [Fact]
    public async Task The_shipped_default_band_still_refuses_an_order_of_magnitude_contradiction()
    {
        var policy = new TreasuryRateExchangePolicy(new ExchangeOptions());

        var decision = await policy.EvaluateAsync(
            Evaluation(platformAmount: 500, projectUnitAmount: 250, rate: 20_000m));

        decision.IsError.Should().BeTrue();
        decision.Message.Should().StartWith(TreasuryRateExchangePolicy.InconsistentTripleMessage);
    }

    [Fact]
    public async Task A_plainer_refusal_is_still_read_first_when_a_stale_rate_is_also_inconsistent()
    {
        // Order matters to the person reading it: "your rate is a day old" is a plainer answer
        // than an arithmetic one, and the band must not take that sentence away.
        var policy = new TreasuryRateExchangePolicy(
            PinnedOptions(maxDeclaredRateAge: TimeSpan.FromHours(1)));

        var decision = await policy.EvaluateAsync(
            Evaluation(
                platformAmount: 1000,
                projectUnitAmount: 5_000_000,
                declaredAt: Now.AddHours(-9)));

        decision.IsError.Should().BeTrue();
        decision.Message.Should().Contain("older");
    }

    [Fact]
    public async Task An_exchange_a_node_prices_at_nothing_has_no_triple_to_contradict()
    {
        var policy = new TreasuryRateExchangePolicy(PinnedOptions(requireDeclaredRate: false));

        var decision = await policy.EvaluateAsync(
            Evaluation(platformAmount: 1000, projectUnitAmount: 5_000_000) with
            {
                DeclaredRate = null,
            });

        decision.IsError.Should().BeFalse(
            decision.Message + " -- there is no declared rate for the amounts to contradict");
        decision.Result!.DeclaredRate.Should().BeNull();
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>The band's answer for one triple against a PINNED platform unit (1 minor unit).</summary>
    private static RateConsistencyOutcome Band(
        long platformAmount,
        long projectUnitAmount,
        decimal rate,
        decimal dust = 0m)
        => ExchangeRateConsistencyBand.Decide(
            new BigInteger(platformAmount),
            new BigInteger(projectUnitAmount),
            rate,
            minMinorUnitsPerPlatformBaseUnit: 1m,
            maxMinorUnitsPerPlatformBaseUnit: 1m,
            dustMinorUnits: dust);

    /// <summary>Options for an operator who has pinned the platform unit at one minor unit.</summary>
    private static ExchangeOptions PinnedOptions(
        decimal lowest = 1m,
        TimeSpan? maxDeclaredRateAge = null,
        bool requireDeclaredRate = true)
        => new()
        {
            MinMinorUnitsPerPlatformBaseUnit = lowest,
            MaxMinorUnitsPerPlatformBaseUnit = 1m,
            RateConsistencyDustMinorUnits = 0m,
            MaxDeclaredRateAge = maxDeclaredRateAge ?? TimeSpan.FromHours(24),
            RequireDeclaredRate = requireDeclaredRate,
        };

    private static ExchangeRateEvaluation Evaluation(
        long platformAmount,
        long projectUnitAmount,
        decimal rate = 0.02m,
        DateTimeOffset? declaredAt = null)
        => new(
            Chain,
            ExchangeDirection.Buy,
            ProjectAsset,
            PlatformAsset,
            new BigInteger(platformAmount),
            new BigInteger(projectUnitAmount),
            new TenantDeclaredRate(
                Chain,
                ProjectAsset,
                "USD",
                rate,
                declaredAt ?? Now.AddMinutes(-5),
                "ardanova:treasury-rate",
                "1"),
            Now);
}
