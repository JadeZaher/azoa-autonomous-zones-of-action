using AZOA.WebAPI.Providers.Blockchain.Algorand;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class TinymanV2MathTests
{
    [Fact]
    public void ExactDivisionStillSubtractsTheValidatorRoundingUnit()
    {
        TinymanV2Math.TryQuote(1_000, 997, 2_000, 30, 6, out var quote).Should().BeTrue();
        quote.Should().Be(new TinymanV2Math.Quote(1_000, 999, 3, 0, 3));
    }

    [Fact]
    public void CurrentPoolFeeChangesOutputAndProtocolSplit()
    {
        TinymanV2Math.TryQuote(10_000, 1_000_000, 1_000_000, 30, 6, out var first).Should().BeTrue();
        TinymanV2Math.TryQuote(10_000, 1_000_000, 1_000_000, 100, 10, out var changed).Should().BeTrue();
        changed!.Output.Should().BeLessThan(first!.Output);
        changed.TotalFee.Should().Be(100);
        changed.ProtocolFee.Should().Be(10);
        changed.PoolFee.Should().Be(90);
    }

    [Fact]
    public void ProductsBeyondUInt64UseExactIntermediateArithmetic()
    {
        TinymanV2Math.TryQuote(1_000_000_000, 10_000_000_000_000_000_000,
            10_000_000_000_000_000_000, 30, 6, out var quote).Should().BeTrue();
        quote!.Output.Should().Be(996_999_999);
    }

    [Theory]
    [InlineData(1UL, 1_000UL, 1_000UL, 30UL, 6UL)]
    [InlineData(1_000UL, 0UL, 1_000UL, 30UL, 6UL)]
    [InlineData(1_000UL, ulong.MaxValue, 1_000UL, 30UL, 6UL)]
    [InlineData(1_000UL, 1_000UL, 1_000UL, 0UL, 6UL)]
    [InlineData(1_000UL, 1_000UL, 1_000UL, 101UL, 6UL)]
    [InlineData(1_000UL, 1_000UL, 1_000UL, 30UL, 2UL)]
    public void ValidatorInvalidOrOverflowingQuoteRefuses(ulong input, ulong reserveIn,
        ulong reserveOut, ulong fee, ulong ratio)
    {
        TinymanV2Math.TryQuote(input, reserveIn, reserveOut, fee, ratio, out var quote).Should().BeFalse();
        quote.Should().BeNull();
    }
}
