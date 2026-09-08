// SPDX-License-Identifier: UNLICENSED
// W8.2 — the buy-back caps, as arithmetic. These are the numbers that decide whether a person
// selling their stake is paid now, paid less, or told their place in a queue, so they are tested
// without a container, a chain or a store: the policy's promises must hold on their own.

using System.Numerics;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Xunit;

namespace AZOA.WebAPI.Tests.Exchange;

public class BuyBackCapsTests
{
    // A treasury of 1,000,000 base units with no holds, no floor and nobody ahead in the queue.
    private const long Treasury = 1_000_000;

    private static BuyBackCapOutcome Decide(
        long requested,
        long treasury = Treasury,
        long held = 0,
        long floor = 0,
        long reserved = 0,
        long? perRequestCeiling = null,
        decimal sharePercent = 25m)
        => BuyBackCaps.Decide(
            requested,
            treasury,
            held,
            floor,
            reserved,
            perRequestCeiling is { } ceiling ? new BigInteger(ceiling) : null,
            sharePercent);

    // ─── ShareOf ───

    [Fact]
    public void ShareOf_TakesTheStatedPercentage_RoundedDown()
        => BuyBackCaps.ShareOf(1_000_000, 25m).Should().Be(250_000);

    [Fact]
    public void ShareOf_RoundsDownRatherThanUp()
        // 1/3 of 100 is 33.33…; paying 34 would pay a base unit nobody funded.
        => BuyBackCaps.ShareOf(100, 33.33m).Should().Be(33);

    [Fact]
    public void ShareOf_AtOrAbove100PercentIsTheWholeBalance()
    {
        BuyBackCaps.ShareOf(1_000_000, 100m).Should().Be(1_000_000);
        BuyBackCaps.ShareOf(1_000_000, 250m).Should().Be(1_000_000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ShareOf_NonPositivePercentagePaysNothing(int percent)
        // The fail-closed reading of a misconfigured cap. It is NEVER read as "no limit".
        => BuyBackCaps.ShareOf(1_000_000, percent).Should().Be(BigInteger.Zero);

    [Fact]
    public void ShareOf_AtOrBelowZeroBalancePaysNothing()
    {
        BuyBackCaps.ShareOf(BigInteger.Zero, 25m).Should().Be(BigInteger.Zero);
        BuyBackCaps.ShareOf(BigInteger.MinusOne, 25m).Should().Be(BigInteger.Zero);
    }

    [Fact]
    public void ShareOf_KeepsPrecisionOnABalanceDecimalCannotHold()
    {
        // 10^30 base units is well past decimal's exact-integer range. Casting to decimal to do
        // the percentage would silently round it; the whole-number path must not.
        var balance = BigInteger.Pow(10, 30);

        BuyBackCaps.ShareOf(balance, 50m).Should().Be(balance / 2);
    }

    // ─── Decide: the ceiling ───

    [Fact]
    public void Decide_AbovePerRequestCeiling_IsRefusedOutrightNotQueued()
    {
        var outcome = Decide(requested: 500, perRequestCeiling: 100);

        // Queuing this would never help: no amount of waiting makes one request smaller.
        outcome.Verdict.Should().Be(BuyBackCapVerdict.AbovePerRequestCeiling);
        outcome.Allowance.Should().Be(100);
        outcome.Shortfall.Should().Be(BigInteger.Zero);
    }

    [Fact]
    public void Decide_AtThePerRequestCeiling_IsStillFillable()
        => Decide(requested: 100, perRequestCeiling: 100).Verdict
            .Should().Be(BuyBackCapVerdict.Fillable);

    // ─── Decide: what is actually spendable ───

    [Fact]
    public void Decide_WithinTheShareCap_IsFillable()
    {
        var outcome = Decide(requested: 250_000);

        outcome.Verdict.Should().Be(BuyBackCapVerdict.Fillable);
        outcome.Allowance.Should().Be(250_000);
        outcome.Shortfall.Should().Be(BigInteger.Zero);
    }

    [Fact]
    public void Decide_AboveTheShareCap_IsShortByTheDifference()
    {
        var outcome = Decide(requested: 400_000);

        outcome.Verdict.Should().Be(BuyBackCapVerdict.Short);
        outcome.Allowance.Should().Be(250_000);
        outcome.Shortfall.Should().Be(150_000);
    }

    [Fact]
    public void Decide_TreasuryFloorIsNotSpendable()
    {
        // 1,000,000 held, but 900,000 is the floor the project keeps to operate on: only
        // 100,000 can pay a seller, even though the 25% share would allow 250,000.
        var outcome = Decide(requested: 250_000, floor: 900_000);

        outcome.Verdict.Should().Be(BuyBackCapVerdict.Short);
        outcome.Allowance.Should().Be(100_000);
        outcome.Shortfall.Should().Be(150_000);
    }

    [Fact]
    public void Decide_HeldUnitsAreNotSpendable()
    {
        var outcome = Decide(requested: 250_000, held: 950_000);

        outcome.Verdict.Should().Be(BuyBackCapVerdict.Short);
        outcome.Allowance.Should().Be(50_000);
        outcome.Shortfall.Should().Be(200_000);
    }

    [Fact]
    public void Decide_AFloorAboveTheBalanceSpendsNothingRatherThanGoingNegative()
    {
        var outcome = Decide(requested: 1, floor: 5_000_000);

        outcome.Verdict.Should().Be(BuyBackCapVerdict.Short);
        outcome.Allowance.Should().Be(BigInteger.Zero);
        outcome.Shortfall.Should().Be(1);
    }

    // ─── Decide: the queue ahead of you is real ───

    [Fact]
    public void Decide_SellersAheadInTheQueueAreSubtractedFirst()
    {
        // 250,000 is allowed; 200,000 is already owed to people who queued earlier. A request
        // for 100,000 does not fit behind them, and being told so is the whole point of a
        // head-of-line queue: an early place must actually mean being paid first.
        var outcome = Decide(requested: 100_000, reserved: 200_000);

        outcome.Verdict.Should().Be(BuyBackCapVerdict.Short);
        outcome.Allowance.Should().Be(250_000);
        outcome.Reserved.Should().Be(200_000);
        outcome.Shortfall.Should().Be(50_000);
    }

    [Fact]
    public void Decide_FitsExactlyBehindTheQueue_IsFillable()
    {
        var outcome = Decide(requested: 50_000, reserved: 200_000);

        outcome.Verdict.Should().Be(BuyBackCapVerdict.Fillable);
        outcome.Shortfall.Should().Be(BigInteger.Zero);
    }

    [Fact]
    public void Decide_AnEmptyTreasuryFillsNothingRatherThanRefusingTheShape()
    {
        var outcome = Decide(requested: 1, treasury: 0);

        // Short, not AbovePerRequestCeiling: waiting CAN help here, so the seller keeps a place.
        outcome.Verdict.Should().Be(BuyBackCapVerdict.Short);
        outcome.Allowance.Should().Be(BigInteger.Zero);
        outcome.Shortfall.Should().Be(1);
    }

    [Fact]
    public void Decide_AMisconfiguredSharePercentagePaysNothing()
    {
        var outcome = Decide(requested: 1, sharePercent: 0m);

        outcome.Verdict.Should().Be(BuyBackCapVerdict.Short);
        outcome.Allowance.Should().Be(BigInteger.Zero);
    }
}
