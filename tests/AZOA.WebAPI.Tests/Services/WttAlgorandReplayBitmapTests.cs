using AZOA.WebAPI.Providers.Blockchain.Algorand;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttAlgorandReplayBitmapTests
{
    [Theory]
    [InlineData(0UL)]
    [InlineData(7UL)]
    [InlineData(8UL)]
    [InlineData(15239UL)]
    [InlineData(15240UL)]
    [InlineData(ulong.MaxValue)]
    public void Sequence_uses_unsigned_page_and_integer_low_bit_order(ulong sequence)
    {
        var bytes = new byte[1905];
        var blob = new WttAlgorandStorage.Blob(bytes, []);
        Assert.False(WttAlgorandReplayBitmap.IsConsumed(blob, sequence));
        bytes[(int)((sequence / 8) % 1905)] = (byte)(1 << (int)(sequence % 8));
        Assert.True(WttAlgorandReplayBitmap.IsConsumed(blob, sequence));
        Assert.False(WttAlgorandReplayBitmap.IsConsumed(blob, sequence ^ 1UL));
    }

    [Fact]
    public void Foreign_role_and_truncated_storage_are_not_unconsumed_evidence()
    {
        Assert.Throws<InvalidDataException>(() => WttAlgorandReplayBitmap.IsConsumed(new(new byte[1905], "asset"u8.ToArray()), 0));
        Assert.Throws<InvalidDataException>(() => WttAlgorandReplayBitmap.IsConsumed(new(new byte[1904], []), 0));
    }
}
