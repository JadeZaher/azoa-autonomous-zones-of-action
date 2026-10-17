using System.Buffers.Binary;
using System.Numerics;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Strict single-hop Whirlpool route arguments from the pinned Jupiter V6 IDL.</summary>
internal sealed record JupiterRouteData(bool SharedAccounts, byte AuthorityId, bool WhirlpoolV2,
    bool AToB, byte SupplementalTickArrays, ulong InputAmount, ulong QuotedOutput, ushort SlippageBps)
{
    internal int FixedAccountCount => SharedAccounts ? 13 : 9;
    internal ulong MinimumOutput => (ulong)((BigInteger)QuotedOutput * (10_000 - SlippageBps) / 10_000);
}

/// <summary>Decodes only the reviewed Jupiter argument grammar; account validation remains separate.</summary>
internal static class JupiterRouteCodec
{
    private static ReadOnlySpan<byte> RouteDiscriminator => [229, 23, 203, 151, 122, 227, 173, 42];
    private static ReadOnlySpan<byte> SharedDiscriminator => [193, 32, 155, 51, 65, 214, 156, 129];

    internal static bool TryDecode(ReadOnlySpan<byte> data, out JupiterRouteData? route)
    {
        route = null;
        if (data.Length is < 36 or > 44)
            return false;
        var shared = data[..8].SequenceEqual(SharedDiscriminator);
        if (!shared && !data[..8].SequenceEqual(RouteDiscriminator))
            return false;
        var offset = 8;
        var authority = shared ? data[offset++] : (byte)0;
        if (authority > 7 || BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)) != 1)
            return false;
        offset += 4;
        var variant = data[offset++];
        if (variant is not (17 or 47))
            return false;
        var direction = data[offset++];
        if (direction > 1)
            return false;
        byte supplemental = 0;
        if (variant == 47)
        {
            var option = data[offset++];
            if (option > 1)
                return false;
            if (option == 1)
            {
                if (offset + 4 > data.Length)
                    return false;
                var slices = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
                offset += 4;
                if (slices > 1)
                    return false;
                if (slices == 1)
                {
                    if (offset + 2 > data.Length || data[offset++] != 6)
                        return false;
                    supplemental = data[offset++];
                    if (supplemental is < 1 or > 3)
                        return false;
                }
            }
        }

        if (data.Length - offset != 22 || data[offset++] != 100 ||
            data[offset++] != 0 || data[offset++] != 1)
            return false;
        var amount = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
        offset += 8;
        var output = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
        offset += 8;
        var slippage = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
        offset += 2;
        if (data[offset] != 0 || amount == 0 || output == 0 || slippage >= 10_000)
            return false;
        var decoded = new JupiterRouteData(shared, authority, variant == 47, direction == 1,
            supplemental, amount, output, slippage);
        if (decoded.MinimumOutput == 0)
            return false;
        route = decoded;
        return true;
    }
}
