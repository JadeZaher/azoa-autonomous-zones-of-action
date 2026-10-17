using System.Buffers.Binary;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class JupiterRouteCodecTests
{
    [Fact]
    public void PinnedDirectWhirlpoolGoldenDecodesFullWidthInput()
    {
        var bytes = Convert.FromHexString("E517CB977AE3AD2A010000001101640001FFFFFFFFFFFFFFFF1027000000000000320000");
        JupiterRouteCodec.TryDecode(bytes, out var route).Should().BeTrue();
        route!.InputAmount.Should().Be(ulong.MaxValue);
        route.QuotedOutput.Should().Be(10_000);
        route.MinimumOutput.Should().Be(9_950);
        route.FixedAccountCount.Should().Be(9);
        route.AToB.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, true, 3)]
    [InlineData(true, true, 3)]
    public void ReviewedLayoutsDecodeAndEveryTruncationOrAppendRefuses(bool shared, bool v2, byte extra)
    {
        var bytes = Build(shared, v2, extra);
        JupiterRouteCodec.TryDecode(bytes, out var route).Should().BeTrue();
        route!.SharedAccounts.Should().Be(shared);
        route.WhirlpoolV2.Should().Be(v2);
        route.SupplementalTickArrays.Should().Be(extra);
        for (var length = 0; length < bytes.Length; length++)
            JupiterRouteCodec.TryDecode(bytes.AsSpan(0, length), out _).Should().BeFalse();
        JupiterRouteCodec.TryDecode([.. bytes, 0], out _).Should().BeFalse();
        JupiterRouteCodec.TryDecode([.. bytes, .. bytes], out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(8, 255)]
    [InlineData(12, 46)]
    [InlineData(13, 2)]
    [InlineData(14, 99)]
    [InlineData(15, 1)]
    [InlineData(16, 0)]
    [InlineData(35, 1)]
    public void UnknownVariantNoncanonicalBoolSplitTopologyOrFeeRefuses(int offset, byte replacement)
    {
        var bytes = Build(false, false, 0);
        bytes[offset] = replacement;
        JupiterRouteCodec.TryDecode(bytes, out _).Should().BeFalse();
    }

    [Fact]
    public void SharedAuthorityAndHookSliceAreNotUnbounded()
    {
        var shared = Build(true, true, 3);
        shared[8] = 8;
        JupiterRouteCodec.TryDecode(shared, out _).Should().BeFalse();
        var hook = Build(false, true, 3);
        hook[19] = 0;
        JupiterRouteCodec.TryDecode(hook, out _).Should().BeFalse();
        var excessive = Build(false, true, 3);
        excessive[20] = 4;
        JupiterRouteCodec.TryDecode(excessive, out _).Should().BeFalse();
    }

    [Fact]
    public void MaximumSlippageAndZeroEconomicValuesRefuse()
    {
        var bytes = Build(false, false, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 3, 2), 10_000);
        JupiterRouteCodec.TryDecode(bytes, out _).Should().BeFalse();
        bytes = Build(false, false, 0);
        bytes.AsSpan(17, 8).Clear();
        JupiterRouteCodec.TryDecode(bytes, out _).Should().BeFalse();
    }

    private static byte[] Build(bool shared, bool v2, byte extra)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(shared
            ? new byte[] { 193, 32, 155, 51, 65, 214, 156, 129 }
            : new byte[] { 229, 23, 203, 151, 122, 227, 173, 42 });
        if (shared) writer.Write((byte)7);
        writer.Write(1U);
        writer.Write(v2 ? (byte)47 : (byte)17);
        writer.Write((byte)1);
        if (v2)
        {
            writer.Write(extra == 0 ? (byte)0 : (byte)1);
            if (extra != 0)
            {
                writer.Write(1U);
                writer.Write((byte)6);
                writer.Write(extra);
            }
        }
        writer.Write((byte)100);
        writer.Write((byte)0);
        writer.Write((byte)1);
        writer.Write(1_000UL);
        writer.Write(10_000UL);
        writer.Write((ushort)50);
        writer.Write((byte)0);
        return stream.ToArray();
    }
}
