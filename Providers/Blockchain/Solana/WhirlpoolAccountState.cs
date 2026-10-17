using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

internal sealed record WhirlpoolPoolState(string Address, string Configuration, ushort TickSpacing, ushort FeeTierIndex,
    ushort FeeRate, ushort ProtocolFeeRate, BigInteger Liquidity, BigInteger SqrtPrice, int CurrentTick,
    string MintA, string VaultA, string MintB, string VaultB);

/// <summary>Decodes pinned Whirlpool pool and fixed/dynamic tick accounts with exact PDA affiliation.</summary>
internal static class WhirlpoolAccountState
{
    internal static readonly PublicKey Program = new("whirLbMiicVdio4qvUfM5KAg6Ct8VwpYzGff3uctyCc");
    private const int MinimumTick = -443636;
    private const int MaximumTick = 443636;

    internal static WhirlpoolPoolState ReadPool(string address, ReadOnlySpan<byte> data)
    {
        if (data.Length != 653 || !HasDiscriminator(data, "Whirlpool"))
            throw new InvalidDataException("Whirlpool account framing differs from the pinned layout.");
        var configuration = Key(data.Slice(8, 32));
        var spacing = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(41, 2));
        var tier = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(43, 2));
        var tick = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(81, 4));
        var mintA = Key(data.Slice(101, 32));
        var mintB = Key(data.Slice(181, 32));
        var expected = Derive([Encoding.UTF8.GetBytes("whirlpool"), new PublicKey(configuration).KeyBytes,
            new PublicKey(mintA).KeyBytes, new PublicKey(mintB).KeyBytes, data.Slice(43, 2).ToArray()], out var bump);
        var sqrt = new BigInteger(data.Slice(65, 16), isUnsigned: true, isBigEndian: false);
        if (spacing == 0 || tick is < MinimumTick or > MaximumTick || sqrt == 0 ||
            data.Slice(101, 32).SequenceCompareTo(data.Slice(181, 32)) >= 0 ||
            expected != address || bump != data[40])
            throw new InvalidDataException("Whirlpool address, seed, mint ordering or tick state is invalid.");
        var vaultA = Key(data.Slice(133, 32));
        var vaultB = Key(data.Slice(213, 32));
        if (vaultA == vaultB || vaultA == address || vaultB == address)
            throw new InvalidDataException("Whirlpool vault roles collide.");
        return new(address, configuration, spacing, tier,
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(45, 2)), BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(47, 2)),
            new BigInteger(data.Slice(49, 16), isUnsigned: true, isBigEndian: false), sqrt, tick, mintA, vaultA, mintB, vaultB);
    }

    internal static int ReadTickArray(string address, ReadOnlySpan<byte> data, WhirlpoolPoolState pool)
    {
        if (data.Length < 12) throw new InvalidDataException("Whirlpool tick array is truncated.");
        var start = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(8, 4));
        var span = checked(88 * pool.TickSpacing);
        var minimumStart = (int)Math.Floor((double)MinimumTick / span) * span;
        if (start < minimumStart || start > MaximumTick || start % span != 0 ||
            address != TickArrayAddress(pool.Address, start)) throw new InvalidDataException("Whirlpool tick array PDA or range differs.");
        if (HasDiscriminator(data, "TickArray"))
        {
            if (data.Length != 9988 || Key(data.Slice(9956, 32)) != pool.Address)
                throw new InvalidDataException("Fixed tick array belongs to another pool or layout.");
            for (var index = 0; index < 88; index++)
                if (data[12 + index * 113] > 1) throw new InvalidDataException("Fixed tick initialization flag is not canonical.");
            return start;
        }
        if (!HasDiscriminator(data, "DynamicTickArray") || data.Length is < 148 or > 10004 ||
            Key(data.Slice(12, 32)) != pool.Address) throw new InvalidDataException("Dynamic tick array belongs to another pool or layout.");
        var bitmap = new BigInteger(data.Slice(44, 16), isUnsigned: true, isBigEndian: false);
        if ((bitmap >> 88) != 0) throw new InvalidDataException("Dynamic tick bitmap exceeds the fixed tick count.");
        var offset = 60;
        for (var index = 0; index < 88; index++)
        {
            var initialized = (bitmap & (BigInteger.One << index)) != 0;
            var size = initialized ? 113 : 1;
            if (offset + size > data.Length || data[offset] != (initialized ? 1 : 0))
                throw new InvalidDataException("Dynamic tick tags and bitmap differ or are truncated.");
            offset += size;
        }
        if (offset != data.Length) throw new InvalidDataException("Dynamic tick array contains trailing data.");
        return start;
    }

    internal static string TickArrayAddress(string pool, int start)
        => Derive([Encoding.UTF8.GetBytes("tick_array"), new PublicKey(pool).KeyBytes,
            Encoding.UTF8.GetBytes(start.ToString(CultureInfo.InvariantCulture))], out _);

    internal static string OracleAddress(string pool)
        => Derive([Encoding.UTF8.GetBytes("oracle"), new PublicKey(pool).KeyBytes], out _);

    private static string Derive(IList<byte[]> seeds, out byte bump)
    {
        if (!PublicKey.TryFindProgramAddress(seeds, Program, out var address, out bump))
            throw new InvalidDataException("Whirlpool PDA derivation failed.");
        return address.Key;
    }

    private static string Key(ReadOnlySpan<byte> bytes) => new PublicKey(bytes.ToArray()).Key;
    private static bool HasDiscriminator(ReadOnlySpan<byte> data, string name)
        => data.Length >= 8 && data[..8].SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes("account:" + name)).AsSpan(0, 8));
}
