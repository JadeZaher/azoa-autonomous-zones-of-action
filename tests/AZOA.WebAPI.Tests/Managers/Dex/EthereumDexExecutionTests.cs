using System.Globalization;
using System.Numerics;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class EthereumDexExecutionTests
{
    [Fact]
    public void Static_tuple_preserves_full_uint256_and_exact_solidity_field_order()
    {
        var maximum = ((BigInteger.One << 256) - 1).ToString(CultureInfo.InvariantCulture);
        var value = Execution() with { GrossInput = maximum };
        var payload = EthereumDexExecutionCodec.Encode(value);
        payload.Should().HaveCount(19 * 32);
        payload[..32].Should().Equal(Convert.FromHexString(value.Operation));
        payload[32..64].Should().Equal(Convert.FromHexString(value.QuoteHash));
        payload[(8 * 32)..(9 * 32)].Should().OnlyContain(item => item == 255);
        payload[(11 * 32)..(12 * 32)].Should().Equal(new byte[29].Concat(new byte[] { 0, 11, 184 }));
        payload[(14 * 32)..(15 * 32)].Should().Equal(Convert.FromHexString(value.PolicyKeyVersion));
        payload[(17 * 32)..].Should().OnlyContain(item => item == 0);
        EthereumDexExecutionCodec.OperationIdentity("operation").Should().NotBe(EthereumDexExecutionCodec.OperationIdentity("other"));
    }

    [Theory]
    [InlineData("01")]
    [InlineData("-1")]
    [InlineData("1.0")]
    [InlineData(" 1")]
    [InlineData("115792089237316195423570985008687907853269984665640564039457584007913129639936")]
    public void Ambiguous_or_overflow_input_never_reaches_ABI_encoding(string amount)
    {
        Action encode = () => EthereumDexExecutionCodec.Encode(Execution() with { GrossInput = amount });
        encode.Should().Throw<InvalidDataException>();
    }

    private static EthereumDexExecution Execution() => new(new string('1', 64), new string('2', 64), Address(1), Address(1),
        Address(2), Address(3), Address(4), Address(5), "1010", "10", "1990", 3000, "2000000000", 1,
        new string('3', 64), new string('4', 64), new string('5', 64), false, false);
    private static string Address(int value) => "0x" + value.ToString("x40", CultureInfo.InvariantCulture);
}
