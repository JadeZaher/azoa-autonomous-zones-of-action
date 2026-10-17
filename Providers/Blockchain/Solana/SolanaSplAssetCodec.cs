using System.Buffers.Binary;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Strict initialized legacy SPL mint and token-account layouts.</summary>
internal static class SolanaSplAssetCodec
{
    internal sealed record Mint(byte Decimals, ulong Supply, string? MintAuthority, string? FreezeAuthority);
    internal sealed record Holding(string Mint, string Owner, ulong Amount, string? Delegate, ulong DelegatedAmount, bool Frozen,
        bool Native, string? CloseAuthority);

    public static Mint ReadMint(ReadOnlySpan<byte> data)
    {
        if (data.Length != 82 || data[45] != 1) throw new InvalidDataException("An initialized legacy SPL mint is required.");
        return new(data[44], BinaryPrimitives.ReadUInt64LittleEndian(data[36..]), KeyOption(data[..36]), KeyOption(data[46..]));
    }

    public static Holding ReadHolding(ReadOnlySpan<byte> data)
    {
        if (data.Length != 165 || data[108] is not (1 or 2)) throw new InvalidDataException("An initialized legacy SPL token account is required.");
        var nativeTag = BinaryPrimitives.ReadUInt32LittleEndian(data[109..]);
        if (nativeTag > 1) throw new InvalidDataException("SPL native option is invalid.");
        return new(new PublicKey(data[..32].ToArray()).Key, new PublicKey(data.Slice(32, 32).ToArray()).Key,
            BinaryPrimitives.ReadUInt64LittleEndian(data[64..]), KeyOption(data.Slice(72, 36)),
            BinaryPrimitives.ReadUInt64LittleEndian(data[121..]), data[108] == 2, nativeTag == 1, KeyOption(data[129..]));
    }

    private static string? KeyOption(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32LittleEndian(bytes) switch
    {
        0 => null,
        1 => new PublicKey(bytes.Slice(4, 32).ToArray()).Key,
        _ => throw new InvalidDataException("SPL public-key option is invalid."),
    };
}
