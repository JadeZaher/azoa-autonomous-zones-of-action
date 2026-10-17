using Solnet.Wallet;
using Solnet.Wallet.Utilities;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Strict legacy/version-zero message framing and exact detached-signature envelope assembly.</summary>
public static class SolanaMessageEnvelope
{
    public sealed record Instruction(byte ProgramIndex, byte[] AccountIndices, byte[] Data);
    public sealed record Lookup(string Address, byte[] WritableIndices, byte[] ReadonlyIndices);
    public sealed record Parsed(bool VersionZero, byte RequiredSignatures, byte ReadonlySigned, byte ReadonlyUnsigned,
        IReadOnlyList<PublicKey> StaticKeys, byte[] Blockhash, IReadOnlyList<Instruction> Instructions, IReadOnlyList<Lookup> Lookups)
    {
        public IReadOnlyList<PublicKey> RequiredKeys => StaticKeys.Take(RequiredSignatures).ToArray();
    }

    public static Parsed Read(byte[] bytes)
    {
        if (bytes is null || bytes.Length is < 4 or > 65535) throw new InvalidDataException("Invalid Solana message length.");
        var reader = new Reader(bytes);
        var first = reader.Byte(); var versioned = (first & 128) != 0;
        if (versioned && first != 128) throw new InvalidDataException("Unsupported Solana message version.");
        var required = versioned ? reader.Byte() : first;
        var readonlySigned = reader.Byte(); var readonlyUnsigned = reader.Byte();
        var keyCount = reader.Short();
        if (required == 0 || required > 127 || keyCount > 256 || required > keyCount
            || readonlySigned >= required || readonlyUnsigned > keyCount - required)
            throw new InvalidDataException("Invalid Solana signer/static-account header.");
        var keys = new PublicKey[keyCount];
        for (var index = 0; index < keys.Length; index++) keys[index] = new PublicKey(reader.Take(32));
        if (keys.Select(key => key.Key).Distinct(StringComparer.Ordinal).Count() != keys.Length)
            throw new InvalidDataException("Duplicate static account keys are not permitted.");
        var blockhash = reader.Take(32);
        var instructions = new Instruction[reader.Short()];
        for (var index = 0; index < instructions.Length; index++)
            instructions[index] = new(reader.Byte(), reader.Take(reader.Short()), reader.Take(reader.Short()));
        var lookups = new Lookup[versioned ? reader.Short() : 0];
        for (var index = 0; index < lookups.Length; index++)
        {
            var table = new PublicKey(reader.Take(32)).Key;
            var writable = reader.Take(reader.Short()); var readOnly = reader.Take(reader.Short());
            var indices = writable.Concat(readOnly).ToArray();
            if (indices.Length == 0 || indices.Distinct().Count() != indices.Length)
                throw new InvalidDataException("Lookup table indices must be nonempty and distinct.");
            lookups[index] = new(table, writable, readOnly);
        }
        if (!reader.End || lookups.Select(value => value.Address).Distinct(StringComparer.Ordinal).Count() != lookups.Length)
            throw new InvalidDataException("Trailing bytes or duplicate lookup tables are not permitted.");
        var totalKeys = keyCount + lookups.Sum(value => value.WritableIndices.Length + value.ReadonlyIndices.Length);
        if (totalKeys > 256 || instructions.Any(value => value.ProgramIndex == 0 || value.ProgramIndex >= keyCount
                || value.AccountIndices.Any(index => index >= totalKeys)))
            throw new InvalidDataException("Compiled instruction indices exceed their static or loaded account role.");
        return new(versioned, required, readonlySigned, readonlyUnsigned, keys, blockhash, instructions, lookups);
    }

    public static IReadOnlyList<PublicKey> Resolve(Parsed message, IReadOnlyList<SolanaLookupTableSnapshot> snapshots)
    {
        if (snapshots.Count != message.Lookups.Count || snapshots.Select(value => value.Address).Distinct(StringComparer.Ordinal).Count() != snapshots.Count)
            throw new InvalidDataException("Exact saved lookup-table snapshots are required.");
        var writable = new List<PublicKey>(); var readOnly = new List<PublicKey>();
        for (var index = 0; index < message.Lookups.Count; index++)
        {
            var lookup = message.Lookups[index]; var saved = snapshots[index];
            if (lookup.Address != saved.Address) throw new InvalidDataException("Lookup table order differs from the signed message.");
            var addresses = saved.ReadActiveAddresses();
            if (lookup.WritableIndices.Concat(lookup.ReadonlyIndices).Any(value => value >= addresses.Count))
                throw new InvalidDataException("Lookup references an absent or not-yet-active address.");
            writable.AddRange(lookup.WritableIndices.Select(value => addresses[value]));
            readOnly.AddRange(lookup.ReadonlyIndices.Select(value => addresses[value]));
        }
        var result = message.StaticKeys.Concat(writable).Concat(readOnly).ToArray();
        if (result.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new InvalidDataException("Loaded accounts collide with another loaded or static account role.");
        return result;
    }

    public static byte[] CompileVersionZero(byte[] canonicalLegacyMessage, IReadOnlyList<SolanaLookupTableSnapshot> tables,
        out IReadOnlyList<SolanaLookupTableSnapshot> usedTables)
    {
        var original = Read(canonicalLegacyMessage);
        if (original.VersionZero || tables.Select(value => value.Address).Distinct(StringComparer.Ordinal).Count() != tables.Count)
            throw new InvalidDataException("Compression requires one legacy message and distinct lookup snapshots.");
        var invoked = original.Instructions.Select(value => (int)value.ProgramIndex).ToHashSet();
        var extracted = new HashSet<int>(); var writableOld = new List<int>(); var readonlyOld = new List<int>();
        var lookups = new List<Lookup>(); var used = new List<SolanaLookupTableSnapshot>();
        foreach (var table in tables)
        {
            var addresses = table.ReadActiveAddresses();
            var writable = new List<byte>(); var readOnly = new List<byte>();
            for (var oldIndex = (int)original.RequiredSignatures; oldIndex < original.StaticKeys.Count; oldIndex++)
            {
                if (invoked.Contains(oldIndex) || extracted.Contains(oldIndex)) continue;
                var addressIndex = -1;
                for (var candidate = 0; candidate < addresses.Count; candidate++)
                    if (addresses[candidate].Key == original.StaticKeys[oldIndex].Key) { addressIndex = candidate; break; }
                if (addressIndex < 0) continue;
                extracted.Add(oldIndex);
                if (oldIndex < original.StaticKeys.Count - original.ReadonlyUnsigned)
                { writable.Add((byte)addressIndex); writableOld.Add(oldIndex); }
                else { readOnly.Add((byte)addressIndex); readonlyOld.Add(oldIndex); }
            }
            if (writable.Count + readOnly.Count == 0) continue;
            lookups.Add(new(table.Address, writable.ToArray(), readOnly.ToArray())); used.Add(table);
        }
        var staticOld = Enumerable.Range(0, original.StaticKeys.Count).Where(index => !extracted.Contains(index)).ToArray();
        var ordered = staticOld.Concat(writableOld).Concat(readonlyOld).ToArray();
        var remap = ordered.Select((oldIndex, newIndex) => (oldIndex, newIndex)).ToDictionary(value => value.oldIndex, value => (byte)value.newIndex);
        var message = new Parsed(true, original.RequiredSignatures, original.ReadonlySigned,
            checked((byte)staticOld.Count(index => index >= original.StaticKeys.Count - original.ReadonlyUnsigned)),
            staticOld.Select(index => original.StaticKeys[index]).ToArray(), original.Blockhash,
            original.Instructions.Select(value => new Instruction(remap[value.ProgramIndex], value.AccountIndices.Select(index => remap[index]).ToArray(), value.Data)).ToArray(), lookups);
        var compiled = Write(message); usedTables = used;
        var resolved = Resolve(Read(compiled), used);
        if (!resolved.Select(value => value.Key).SequenceEqual(ordered.Select(index => original.StaticKeys[index].Key)))
            throw new InvalidDataException("Versioned compilation changed resolved instruction identities.");
        return compiled;
    }

    public static byte[] Populate(byte[] message, IReadOnlyList<byte[]> signatures)
    {
        var parsed = Read(message);
        if (signatures.Count != parsed.RequiredSignatures || signatures.Any(signature => signature.Length != 64))
            throw new InvalidDataException("Exact ordered signatures are required for the compiled message.");
        for (var index = 0; index < signatures.Count; index++)
            if (!parsed.StaticKeys[index].Verify(message, signatures[index])) throw new InvalidDataException("Signature does not authorize this message.");
        using var stream = new MemoryStream(); WriteShort(stream, signatures.Count);
        foreach (var signature in signatures) stream.Write(signature);
        stream.Write(message);
        if (stream.Length > 1232) throw new InvalidDataException("Signed transaction exceeds the Solana packet ceiling.");
        return stream.ToArray();
    }

    public static string VerifyWire(byte[] wire, byte[] exactMessage)
    {
        if (wire.Length > 1232) throw new InvalidDataException("Signed transaction exceeds the Solana packet ceiling.");
        var reader = new Reader(wire); var count = reader.Short();
        if (count is 0 or > 127) throw new InvalidDataException("Invalid transaction signature count.");
        var signatures = new byte[count][];
        for (var index = 0; index < count; index++) signatures[index] = reader.Take(64);
        if (!reader.Remaining.SequenceEqual(exactMessage) || !Populate(exactMessage, signatures).AsSpan().SequenceEqual(wire))
            throw new InvalidDataException("Signed transaction differs from the exact prepared message.");
        return Encoders.Base58.EncodeData(signatures[0]);
    }

    private static byte[] Write(Parsed message)
    {
        using var stream = new MemoryStream();
        if (message.VersionZero) stream.WriteByte(128);
        stream.WriteByte(message.RequiredSignatures); stream.WriteByte(message.ReadonlySigned); stream.WriteByte(message.ReadonlyUnsigned);
        WriteShort(stream, message.StaticKeys.Count);
        foreach (var key in message.StaticKeys) stream.Write(key.KeyBytes);
        stream.Write(message.Blockhash); WriteShort(stream, message.Instructions.Count);
        foreach (var instruction in message.Instructions)
        {
            stream.WriteByte(instruction.ProgramIndex); WriteShort(stream, instruction.AccountIndices.Length); stream.Write(instruction.AccountIndices);
            WriteShort(stream, instruction.Data.Length); stream.Write(instruction.Data);
        }
        if (message.VersionZero)
        {
            WriteShort(stream, message.Lookups.Count);
            foreach (var lookup in message.Lookups)
            {
                stream.Write(new PublicKey(lookup.Address).KeyBytes); WriteShort(stream, lookup.WritableIndices.Length); stream.Write(lookup.WritableIndices);
                WriteShort(stream, lookup.ReadonlyIndices.Length); stream.Write(lookup.ReadonlyIndices);
            }
        }
        return stream.ToArray();
    }

    private static void WriteShort(Stream stream, int value)
    {
        if (value is < 0 or > ushort.MaxValue) throw new InvalidDataException("Short vector exceeds uint16.");
        do { var part = (byte)(value & 127); value >>= 7; stream.WriteByte(value == 0 ? part : (byte)(part | 128)); } while (value != 0);
    }

    private sealed class Reader(byte[] bytes)
    {
        private int _offset;
        public bool End => _offset == bytes.Length;
        public ReadOnlySpan<byte> Remaining => bytes.AsSpan(_offset);
        public byte Byte() => _offset < bytes.Length ? bytes[_offset++] : throw new InvalidDataException("Truncated Solana message.");
        public byte[] Take(int count)
        {
            if (count < 0 || count > bytes.Length - _offset) throw new InvalidDataException("Truncated Solana message field.");
            var result = bytes.AsSpan(_offset, count).ToArray(); _offset += count; return result;
        }
        public int Short()
        {
            var value = 0;
            for (var index = 0; index < 3; index++)
            {
                var next = Byte();
                if (index == 2 && next > 3 || index > 0 && next == 0) throw new InvalidDataException("Noncanonical or overflowing short vector.");
                value |= (next & 127) << (7 * index);
                if ((next & 128) == 0) return value;
            }
            throw new InvalidDataException("Unterminated short vector.");
        }
    }
}
