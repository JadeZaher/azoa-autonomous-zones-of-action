using System.Buffers.Binary;
using System.Numerics;
using AZOA.WebAPI.Services.Wormhole;
using AZOA.WebAPI.Models.Wtt;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttCodecTests
{
    [Fact]
    public void AlternateGuardianEnvelopesHaveTheSameBodyIdentityAndUnsignedSequence()
    {
        var first = Envelope(0, ulong.MaxValue);
        var alternate = Envelope(17, ulong.MaxValue);
        alternate[1] = 1;
        var parsed = WttVaaCodec.Parse(first);
        var other = WttVaaCodec.Parse(alternate);
        Assert.NotEqual(parsed.EnvelopeHex, other.EnvelopeHex);
        Assert.Equal(parsed.BodyDigest, other.BodyDigest);
        Assert.Equal("18446744073709551615", parsed.Sequence);
        alternate[^1] ^= 1;
        Assert.NotEqual(parsed.BodyDigest, WttVaaCodec.Parse(alternate).BodyDigest);
    }

    [Fact]
    public void TruncatedOrAmbiguousSignatureFramingIsRejected()
    {
        var bytes = Envelope(0, 1);
        Assert.Throws<FormatException>(() => WttVaaCodec.Parse(bytes[..100]));
        bytes[5] = 2;
        Assert.Throws<FormatException>(() => WttVaaCodec.Parse(bytes));
        bytes = Envelope(0, 1);
        bytes[71] = 27;
        Assert.Throws<FormatException>(() => WttVaaCodec.Parse(bytes));
        bytes = Envelope(0, 1);
        bytes[0] = 2;
        Assert.Throws<FormatException>(() => WttVaaCodec.Parse(bytes));
    }

    [Fact]
    public void SolidityPackedTransferLayoutPreservesUint256AndRejectsExtraBytes()
    {
        string maximum = ((BigInteger.One << 256) - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var transfer = new WttTransferPayload(maximum, new string('a', 64), 2, new string('b', 64), 8, "9007199254740993");
        var bytes = WttPayloadCodec.EncodeTransfer(transfer);
        Assert.Equal(133, bytes.Length);
        Assert.Equal(1, bytes[0]);
        Assert.All(bytes[1..33], value => Assert.Equal(255, value));
        Assert.Equal(new byte[] { 0, 2 }, bytes[65..67]);
        Assert.Equal(new byte[] { 0, 8 }, bytes[99..101]);
        Assert.Equal(transfer, WttPayloadCodec.ParseTransfer(bytes));
        Assert.Throws<FormatException>(() => WttPayloadCodec.ParseTransfer([.. bytes, 0]));
        bytes[0] = 3;
        Assert.Throws<FormatException>(() => WttPayloadCodec.ParseTransfer(bytes));
    }

    [Fact]
    public void NormalizationDisclosesDustAndFloorsRelayerFeeIndependently()
    {
        var amounts = WttPayloadCodec.Normalize("1000000000000000001", "19999999999", 18);
        Assert.Equal("1000000000000000000", amounts.Debited);
        Assert.Equal("1", amounts.Dust);
        Assert.Equal("100000000", amounts.Amount);
        Assert.Equal("1", amounts.Fee);
        Assert.Equal("99999999", amounts.RecipientAmount);
        Assert.Equal("1000000000000000000", WttPayloadCodec.Denormalize(amounts.Amount, 18));
        Assert.Equal("100000000", WttPayloadCodec.Denormalize(amounts.Amount, 8));
        Assert.Throws<FormatException>(() => WttPayloadCodec.Normalize("9999999999", "0", 18));
        Assert.Throws<FormatException>(() => WttPayloadCodec.Normalize("1", "2", 8));
    }

    [Theory]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("-1")]
    [InlineData("1.0")]
    [InlineData("115792089237316195423570985008687907853269984665640564039457584007913129639936")]
    public void NoncanonicalOrOverflowAmountsAreRejected(string value)
        => Assert.Throws<FormatException>(() => WttPayloadCodec.ParseAmount(value));

    [Fact]
    public void AttestationKeepsOpaqueMetadataBytesAndExactLength()
    {
        var bytes = new byte[100];
        bytes[0] = 2;
        bytes[34] = 8;
        bytes[35] = 19;
        bytes[36] = 255;
        var attestation = WttPayloadCodec.ParseAttestation(bytes);
        Assert.Equal(19, attestation.Decimals);
        Assert.StartsWith("ff", attestation.SymbolHex);
        Assert.Equal(bytes, WttPayloadCodec.EncodeAttestation(attestation));
        Assert.Throws<FormatException>(() => WttPayloadCodec.ParseAttestation(bytes[..99]));
    }

    private static byte[] Envelope(byte guardian, ulong sequence)
    {
        var result = new byte[6 + 66 + 51 + 133];
        result[0] = 1;
        result[5] = 1;
        result[6] = guardian;
        result[7] = 1;
        result[39] = 1;
        int body = 72;
        result[body + 9] = 2;
        result[body + 41] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(body + 42, 8), sequence);
        WttPayloadCodec.EncodeTransfer(new("100", new string('a', 64), 2, new string('b', 64), 8, "0"))
            .CopyTo(result, body + 51);
        return result;
    }
}
