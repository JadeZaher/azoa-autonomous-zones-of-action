using System.Buffers.Binary;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Exact pinned Solitaire token-bridge instruction data; account roles and authority are validated separately.</summary>
internal static class WttSolanaInstructionData
{
    public static byte[] Transfer(bool wrapped, uint nonce, ulong amount, ulong fee, ReadOnlySpan<byte> recipient, ushort targetChain)
    {
        if (amount == 0 || fee > amount || recipient.Length != 32 || recipient.IndexOfAnyExcept((byte)0) < 0
            || targetChain is 0 or 1) throw new ArgumentException("Invalid Solana WTT transfer arguments.");
        var data = new byte[55];
        data[0] = wrapped ? (byte)4 : (byte)5;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1), nonce);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(5), amount);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(13), fee);
        recipient.CopyTo(data.AsSpan(21));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(53), targetChain);
        return data;
    }

    public static byte[] Attest(uint nonce)
    {
        var data = new byte[5]; data[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1), nonce);
        return data;
    }

    public static byte[] Complete(bool wrapped) => [wrapped ? (byte)3 : (byte)2];
    public static byte[] Register() => [7];
}
