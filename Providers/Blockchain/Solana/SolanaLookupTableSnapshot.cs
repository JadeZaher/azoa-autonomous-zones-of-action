using System.Buffers.Binary;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Exact finalized lookup-table bytes retained with a prepared versioned transaction.</summary>
public sealed record SolanaLookupTableSnapshot(string Address, string Owner, string DataBase64, ulong Slot)
{
    public const string Program = "AddressLookupTab1e1111111111111111111111111";

    /// <summary>Resolves only active entries from this saved snapshot, never a mutable latest table.</summary>
    public IReadOnlyList<PublicKey> ReadActiveAddresses()
    {
        _ = new PublicKey(Address);
        var data = Convert.FromBase64String(DataBase64);
        if (Owner != Program || data.Length < 56 || data.Length > 56 + 256 * 32 || (data.Length - 56) % 32 != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(data) != 1
            || BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(4)) != ulong.MaxValue
            || data[21] > 1)
            throw new InvalidDataException("Lookup table must have the actual owner and an active bounded protocol layout.");
        var extended = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(12));
        var count = (data.Length - 56) / 32;
        if (extended > Slot || data[20] > count)
            throw new InvalidDataException("Lookup table extension metadata exceeds the finalized snapshot.");
        var active = Slot > extended ? count : data[20];
        var result = new PublicKey[active];
        for (var index = 0; index < active; index++) result[index] = new PublicKey(data.AsSpan(56 + index * 32, 32).ToArray());
        return result;
    }
}
