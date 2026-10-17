using System.Globalization;
using System.Numerics;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Deployments;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Helpers;

public sealed class UnsignedIntegerTextTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("18446744073709551615")]
    [InlineData("18446744073709551616")]
    [InlineData("115792089237316195423570985008687907853269984665640564039457584007913129639935")]
    public void CanonicalAmountsPreserveFullWidthAndNativeWrapper(string text)
    {
        UnsignedIntegerText.TryParse(text, out var value).Should().BeTrue();
        value.ToString(CultureInfo.InvariantCulture).Should().Be(text);
        NativeOperationAuthorization.Amount(text, out var native).Should().BeTrue();
        native.Should().Be(value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData("-0")]
    [InlineData("-1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1\n")]
    [InlineData("1.0")]
    [InlineData("1e3")]
    [InlineData("1,000")]
    [InlineData("١")]
    public void NoncanonicalAmountsAreRejected(string? text)
        => UnsignedIntegerText.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public void RangeChecksBelongToProtocolCallers()
    {
        var largerThanUint256 = BigInteger.One << 256;
        UnsignedIntegerText.TryParse(largerThanUint256.ToString(CultureInfo.InvariantCulture), out var parsed)
            .Should().BeTrue();
        parsed.Should().Be(largerThanUint256);
    }
}
