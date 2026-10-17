using System.Buffers.Binary;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Strict pinned Wormhole account layouts; see directory protocol notes.</summary>
internal static class WttSolanaStateCodec
{
    internal static bool TokenConfig(ReadOnlySpan<byte> data, ReadOnlySpan<byte> core, out bool paused)
    {
        paused = false;
        if (core.Length != 32 || data.Length is not (32 or 137) || !data[..32].SequenceEqual(core)
            || data.Length == 137 && data[32] > 1) return false;
        paused = data.Length == 137 && data[32] == 1;
        return true;
    }

    internal static bool GuardianSet(ReadOnlySpan<byte> data, uint index, uint chainTime,
        out IReadOnlyList<string> addresses, out uint expiration)
    {
        addresses = []; expiration = 0;
        if (data.Length < 36 || BinaryPrimitives.ReadUInt32LittleEndian(data) != index) return false;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (count is 0 or > 256 || data.Length != 16 + count * 20) return false;
        var end = 8 + (int)count * 20;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[end..]) > chainTime) return false;
        var values = new string[count];
        for (var i = 0; i < count; i++) values[i] = Convert.ToHexStringLower(data.Slice(8 + i * 20, 20));
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) return false;
        expiration = BinaryPrimitives.ReadUInt32LittleEndian(data[(end + 4)..]);
        addresses = values;
        return true;
    }

    internal static bool Endpoint(ReadOnlySpan<byte> data, ushort chain, ReadOnlySpan<byte> emitter)
        => data.Length == 34 && emitter.Length == 32 && BinaryPrimitives.ReadUInt16LittleEndian(data) == chain
            && data[2..].SequenceEqual(emitter);

    internal static bool Clock(ReadOnlySpan<byte> data, ulong contextSlot, out uint timestamp)
    {
        timestamp = 0;
        if (data.Length != 40 || BinaryPrimitives.ReadUInt64LittleEndian(data) != contextSlot) return false;
        var time = BinaryPrimitives.ReadInt64LittleEndian(data[32..]);
        if (time is < 0 or > uint.MaxValue) return false;
        timestamp = (uint)time;
        return true;
    }
}
