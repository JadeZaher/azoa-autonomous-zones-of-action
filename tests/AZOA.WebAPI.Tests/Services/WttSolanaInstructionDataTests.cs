using System.Buffers.Binary;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttSolanaInstructionDataTests
{
    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 4)]
    public void PinnedTransferEncodingPreservesFullUnsignedAmountsAndLittleEndianFields(bool wrapped, byte discriminator)
    {
        var recipient = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var data = WttSolanaInstructionData.Transfer(wrapped, 0x04030201, ulong.MaxValue, 0x0807060504030201, recipient, 10002);
        Assert.Equal(55, data.Length);
        Assert.Equal(discriminator, data[0]);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, data[1..5]);
        Assert.Equal(Enumerable.Repeat((byte)255, 8), data[5..13]);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, data[13..21]);
        Assert.Equal(recipient, data[21..53]);
        Assert.Equal((ushort)10002, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(53)));
    }

    [Fact]
    public void OtherStagesUseTheirExactPinnedClosedDiscriminators()
    {
        Assert.Equal(new byte[] { 1, 1, 2, 3, 4 }, WttSolanaInstructionData.Attest(0x04030201));
        Assert.Equal(new byte[] { 2 }, WttSolanaInstructionData.Complete(false));
        Assert.Equal(new byte[] { 3 }, WttSolanaInstructionData.Complete(true));
        Assert.Equal(new byte[] { 7 }, WttSolanaInstructionData.Register());
        Assert.Throws<ArgumentException>(() => WttSolanaInstructionData.Transfer(false, 0, 0, 0, new byte[32], 2));
        Assert.Throws<ArgumentException>(() => WttSolanaInstructionData.Transfer(false, 0, 1, 2, new byte[32], 2));
        Assert.Throws<ArgumentException>(() => WttSolanaInstructionData.Transfer(false, 0, 1, 0, new byte[31], 2));
    }
}
