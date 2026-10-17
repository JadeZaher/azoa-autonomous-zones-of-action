using System.Buffers.Binary;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Strict Wormhole wrapped-metadata layout.</summary>
internal static class WttSolanaAssetCodec
{
    internal sealed record Wrapped(ushort Chain, byte[] Address, byte OriginalDecimals, ulong? UpdatedSequence);

    public static Wrapped ReadWrapped(ReadOnlySpan<byte> data)
    {
        if (data.Length is not (35 or 43)) throw new InvalidDataException("Wrapped metadata layout differs from the pinned bridge.");
        var chain = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var address = data.Slice(2, 32).ToArray();
        if (chain is 0 or 1 || address.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            throw new InvalidDataException("Wrapped metadata requires an exact foreign origin.");
        return new(chain, address, data[34], data.Length == 43 ? BinaryPrimitives.ReadUInt64LittleEndian(data[35..]) : null);
    }
}
