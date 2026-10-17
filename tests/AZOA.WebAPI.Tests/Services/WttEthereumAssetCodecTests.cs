using System.Numerics;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttEthereumAssetCodecTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(4096)]
    public void MetadataMatchesSolidityFirstWordWithoutTextNormalization(int length)
    {
        var value = Enumerable.Range(0, length).Select(index => (byte)(index % 255 + 1)).ToArray();
        var encoded = Encode(value);
        Assert.True(WttEthereumAssetCodec.TryMetadataWord(EthereumHex.Data(encoded), out var actual));
        var expected = new byte[32];
        value.AsSpan(0, Math.Min(length, 32)).CopyTo(expected);
        Assert.Equal(Convert.ToHexStringLower(expected), actual);
        for (var truncate = 0; truncate < encoded.Length; truncate++)
            Assert.False(WttEthereumAssetCodec.TryMetadataWord(EthereumHex.Data(encoded[..truncate]), out _));
    }

    [Fact]
    public void RejectsBytes32FallbackOffsetsTrailingAndNonzeroPadding()
    {
        Assert.False(WttEthereumAssetCodec.TryMetadataWord(EthereumHex.Data(new byte[32]), out _));
        var valid = Encode([0x20, 0, 0xff]);
        var changed = valid.ToArray(); changed[31] = 64;
        Assert.False(WttEthereumAssetCodec.TryMetadataWord(EthereumHex.Data(changed), out _));
        changed = valid.ToArray(); changed[^1] = 1;
        Assert.False(WttEthereumAssetCodec.TryMetadataWord(EthereumHex.Data(changed), out _));
        Assert.False(WttEthereumAssetCodec.TryMetadataWord(EthereumHex.Data([.. valid, 0]), out _));
    }

    private static byte[] Encode(byte[] value)
    {
        var bytes = new byte[64 + (value.Length + 31) / 32 * 32];
        bytes[31] = 32;
        var length = new BigInteger(value.Length).ToByteArray(true, true);
        length.CopyTo(bytes, 64 - length.Length);
        value.CopyTo(bytes, 64);
        return bytes;
    }
}
