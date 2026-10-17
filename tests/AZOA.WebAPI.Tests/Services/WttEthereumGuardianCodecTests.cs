using System.Buffers.Binary;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttEthereumGuardianCodecTests
{
    [Fact]
    public void SolidityGuardianTupleDecodesStrictOffsetsAddressArrayAndExpiration()
    {
        var bytes = Tuple();
        Assert.True(EthereumProvider.TryWttGuardianSet(EthereumHex.Data(bytes), out var keys, out var expiry));
        Assert.Equal(2, keys.Count);
        Assert.Equal(new string('1', 40), keys[0]);
        Assert.Equal(new string('2', 40), keys[1]);
        Assert.Equal(uint.MaxValue, expiry);
    }

    [Theory]
    [InlineData(31, 64)]
    [InlineData(63, 96)]
    [InlineData(64, 1)]
    [InlineData(127, 3)]
    [InlineData(128, 1)]
    public void NoncanonicalTupleOffsetsWidthsAndPaddingAreRejected(int position, byte value)
    {
        var bytes = Tuple();
        bytes[position] = value;
        Assert.False(EthereumProvider.TryWttGuardianSet(EthereumHex.Data(bytes), out _, out _));
    }

    [Fact]
    public void DuplicateGuardiansTruncationAndTrailingDataAreRejected()
    {
        var bytes = Tuple();
        Assert.False(EthereumProvider.TryWttGuardianSet(EthereumHex.Data(bytes[..^1]), out _, out _));
        Assert.False(EthereumProvider.TryWttGuardianSet(EthereumHex.Data([.. bytes, 0]), out _, out _));
        bytes.AsSpan(128, 32).CopyTo(bytes.AsSpan(160, 32));
        Assert.False(EthereumProvider.TryWttGuardianSet(EthereumHex.Data(bytes), out _, out _));
    }

    private static byte[] Tuple()
    {
        var bytes = new byte[192];
        bytes[31] = 32;
        bytes[63] = 64;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(92, 4), uint.MaxValue);
        bytes[127] = 2;
        bytes.AsSpan(140, 20).Fill(0x11);
        bytes.AsSpan(172, 20).Fill(0x22);
        return bytes;
    }
}
