using System.Buffers.Binary;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttSolanaAssetCodecTests
{
    [Fact]
    public void MintRequiresInitializedLegacyLayoutAndStrictAuthorityOptions()
    {
        var mint = new byte[82]; mint[44] = 9; mint[45] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(mint.AsSpan(36), ulong.MaxValue);
        Assert.Equal(ulong.MaxValue, SolanaSplAssetCodec.ReadMint(mint).Supply);
        Assert.Null(SolanaSplAssetCodec.ReadMint(mint).FreezeAuthority);
        var extension = new byte[83]; mint.CopyTo(extension, 0);
        Assert.Throws<InvalidDataException>(() => SolanaSplAssetCodec.ReadMint(extension));
        mint[46] = 2;
        Assert.Throws<InvalidDataException>(() => SolanaSplAssetCodec.ReadMint(mint));
    }

    [Fact]
    public void WrappedMetadataRetainsOriginalDecimalsAndSequenceWithoutTruncation()
    {
        var bytes = new byte[43]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, 10002);
        bytes[2] = 1; bytes[34] = 18; BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(35), ulong.MaxValue);
        var parsed = WttSolanaAssetCodec.ReadWrapped(bytes);
        Assert.Equal((ushort)10002, parsed.Chain); Assert.Equal((byte)18, parsed.OriginalDecimals);
        Assert.Equal(ulong.MaxValue, parsed.UpdatedSequence);
        Assert.Null(WttSolanaAssetCodec.ReadWrapped(bytes[..35]).UpdatedSequence);
        Assert.Throws<InvalidDataException>(() => WttSolanaAssetCodec.ReadWrapped(bytes[..42]));
        bytes[0] = 1; bytes[1] = 0;
        Assert.Throws<InvalidDataException>(() => WttSolanaAssetCodec.ReadWrapped(bytes));
    }

    [Fact]
    public void HoldingDisclosesFrozenStateAndRejectsInvalidNativeTag()
    {
        var bytes = new byte[165]; bytes[108] = 2;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64), ulong.MaxValue);
        Assert.True(SolanaSplAssetCodec.ReadHolding(bytes).Frozen);
        Assert.Equal(ulong.MaxValue, SolanaSplAssetCodec.ReadHolding(bytes).Amount);
        bytes[109] = 2;
        Assert.Throws<InvalidDataException>(() => SolanaSplAssetCodec.ReadHolding(bytes));
    }
}
