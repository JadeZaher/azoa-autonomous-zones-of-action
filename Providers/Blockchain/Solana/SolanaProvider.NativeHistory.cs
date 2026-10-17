using System.Buffers.Binary;
using System.Text.Json;
using Solnet.Programs;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private sealed record NativeHistoryInstruction(ulong Slot, int TransactionIndex, int Sequence, string Signature,
        string Program, IReadOnlyList<string> Accounts, byte[] Data)
    {
        public (ulong Slot, int TransactionIndex, int Sequence) Position => (Slot, TransactionIndex, Sequence);
    }

    private async Task<bool> ProveNativeProgramAtCallAsync(string programId, string expectedHash, string signature, CancellationToken ct, bool includeTransaction = false)
    {
        var call = await ReadNativeHistoryTransactionAsync(signature, ct);
        if (call is null) return false;
        var slot = call.Value.GetProperty("slot").GetUInt64();
        var positions = await NativeBlockPositionsAsync(slot, ct);
        if (!positions.TryGetValue(signature, out var position)) return false;
        var dataAddress = SolanaNativeLoader.ProgramData(new PublicKey(programId));
        var current = await ReadNativeAccountAsync(dataAddress.Key, ct);
        if (current is not null && current.Owner == SolanaNativeLoader.Program.Key && current.Data.Length >= SolanaNativeLoader.ProgramDataHeaderSize
            && BinaryPrimitives.ReadUInt32LittleEndian(current.Data) == 3 && current.Data[12] == 0
            && BinaryPrimitives.ReadUInt64LittleEndian(current.Data.AsSpan(4, 8)) < slot)
            return NativeHash(current.Data.AsSpan(SolanaNativeLoader.ProgramDataHeaderSize).ToArray()) == expectedHash;
        var history = await ReadNativeAddressHistoryAsync(dataAddress.Key, slot, ct);
        if (history is null) return false;
        var before = history.Where(item => item.Slot < slot || item.Slot == slot && (item.TransactionIndex < position || includeTransaction && item.TransactionIndex == position))
            .Where(item => item.Program == SolanaNativeLoader.Program.Key && item.Data.Length >= 4).OrderBy(item => item.Position).ToArray();
        ulong capacity = 0;
        NativeHistoryInstruction? version = null;
        foreach (var item in before)
        {
            var operation = BinaryPrimitives.ReadUInt32LittleEndian(item.Data);
            // Agave loader-v3 activates deployed code one slot after its write.
            if (!includeTransaction && item.Slot == slot && operation is 2 or 3) continue;
            if (operation == 2 && item.Accounts.Count >= 4 && item.Accounts[1] == dataAddress.Key && item.Accounts[2] == programId && item.Data.Length == 12)
            { capacity = BinaryPrimitives.ReadUInt64LittleEndian(item.Data.AsSpan(4)); version = item; }
            else if (operation == 3 && item.Accounts.Count >= 3 && item.Accounts[0] == dataAddress.Key && item.Accounts[1] == programId) version = item;
            else if (operation == 6 && item.Accounts.Count >= 2 && item.Accounts[0] == dataAddress.Key && item.Data.Length == 8)
                capacity = checked(capacity + BinaryPrimitives.ReadUInt32LittleEndian(item.Data.AsSpan(4)));
            else if (operation == 5 && item.Accounts.Count > 0 && item.Accounts[0] == dataAddress.Key) { capacity = 0; version = null; }
        }
        if (version is null || capacity is 0 or > 10_485_760) return false;
        var buffer = version.Accounts[BinaryPrimitives.ReadUInt32LittleEndian(version.Data) == 2 ? 3 : 2];
        var bufferHistory = await ReadNativeAddressHistoryAsync(buffer, version.Slot, ct);
        if (bufferHistory is null) return false;
        byte[]? bytes = null;
        foreach (var item in bufferHistory.Where(item => item.Position.CompareTo(version.Position) < 0).OrderBy(item => item.Position))
        {
            if (item.Program == SystemProgram.ProgramIdKey.Key && item.Accounts.Count > 1 && item.Accounts[1] == buffer
                && TryNativeCreatedBufferSize(item.Data, out var bufferLength)) bytes = new byte[bufferLength];
            if (item.Program != SolanaNativeLoader.Program.Key || item.Accounts.Count == 0 || item.Accounts[0] != buffer || item.Data.Length < 4) continue;
            var operation = BinaryPrimitives.ReadUInt32LittleEndian(item.Data);
            if (operation == 5) bytes = null;
            if (operation != 1) continue;
            if (bytes is null || item.Data.Length < 16) return false;
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(item.Data.AsSpan(4));
            var length = BinaryPrimitives.ReadUInt64LittleEndian(item.Data.AsSpan(8));
            if (length > int.MaxValue || item.Data.Length != 16 + (long)length || offset + length > (ulong)bytes.Length) return false;
            item.Data.AsSpan(16).CopyTo(bytes.AsSpan((int)offset));
        }
        if (bytes is null || (ulong)bytes.Length > capacity) return false;
        var programBytes = new byte[checked((int)capacity)];
        bytes.CopyTo(programBytes, 0);
        return NativeHash(programBytes) == expectedHash;
    }

    private async Task<List<NativeHistoryInstruction>?> ReadNativeAddressHistoryAsync(string address, ulong maximumSlot, CancellationToken ct)
    {
        var result = new List<NativeHistoryInstruction>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var blocks = new Dictionary<ulong, Dictionary<string, int>>();
        string? before = null;
        for (var page = 0; page < 128; page++)
        {
            var options = new Dictionary<string, object> { ["commitment"] = "finalized", ["limit"] = 1000 };
            if (before is not null) options["before"] = before;
            var signatures = await DurableRpcAsync("getSignaturesForAddress", [address, options], ct);
            if (signatures.ValueKind != JsonValueKind.Array) return null;
            if (signatures.GetArrayLength() == 0) return result;
            foreach (var item in signatures.EnumerateArray())
            {
                var signature = item.GetProperty("signature").GetString()!;
                if (!seen.Add(signature)) return null;
                before = signature;
                var slot = item.GetProperty("slot").GetUInt64();
                if (slot > maximumSlot || item.GetProperty("err").ValueKind != JsonValueKind.Null) continue;
                var transaction = await ReadNativeHistoryTransactionAsync(signature, ct);
                if (transaction is null || transaction.Value.GetProperty("slot").GetUInt64() != slot) return null;
                if (!blocks.TryGetValue(slot, out var positions)) blocks[slot] = positions = await NativeBlockPositionsAsync(slot, ct);
                if (!positions.TryGetValue(signature, out var position)) return null;
                result.AddRange(NativeTransactionInstructions(transaction.Value, slot, position, signature));
            }
        }
        return null;
    }

    private async Task<JsonElement?> ReadNativeHistoryTransactionAsync(string signature, CancellationToken ct)
    {
        var result = await DurableRpcAsync("getTransaction", [signature, new { encoding = "json", commitment = "finalized", maxSupportedTransactionVersion = 0 }], ct);
        return result.ValueKind == JsonValueKind.Null || result.GetProperty("meta").GetProperty("err").ValueKind != JsonValueKind.Null ? null : result;
    }

    private async Task<Dictionary<string, int>> NativeBlockPositionsAsync(ulong slot, CancellationToken ct)
    {
        var block = await DurableRpcAsync("getBlock", [slot, new { commitment = "finalized", transactionDetails = "signatures", rewards = false }], ct);
        if (block.ValueKind == JsonValueKind.Null || !block.TryGetProperty("signatures", out var signatures)) return new(StringComparer.Ordinal);
        return signatures.EnumerateArray().Select((signature, index) => (Signature: signature.GetString()!, Index: index))
            .ToDictionary(item => item.Signature, item => item.Index, StringComparer.Ordinal);
    }

    private static IEnumerable<NativeHistoryInstruction> NativeTransactionInstructions(JsonElement transaction, ulong slot, int position, string signature)
    {
        var message = transaction.GetProperty("transaction").GetProperty("message");
        var keys = message.GetProperty("accountKeys").EnumerateArray().Select(key => key.GetString()!).ToList();
        var meta = transaction.GetProperty("meta");
        if (meta.TryGetProperty("loadedAddresses", out var loaded))
        {
            keys.AddRange(loaded.GetProperty("writable").EnumerateArray().Select(key => key.GetString()!));
            keys.AddRange(loaded.GetProperty("readonly").EnumerateArray().Select(key => key.GetString()!));
        }
        var inners = meta.TryGetProperty("innerInstructions", out var inner) && inner.ValueKind == JsonValueKind.Array
            ? inner.EnumerateArray().ToDictionary(item => item.GetProperty("index").GetInt32(), item => item.GetProperty("instructions")) : new Dictionary<int, JsonElement>();
        var sequence = 0; var index = 0;
        foreach (var instruction in message.GetProperty("instructions").EnumerateArray())
        {
            yield return Decode(instruction);
            if (inners.TryGetValue(index, out var children)) foreach (var child in children.EnumerateArray()) yield return Decode(child);
            index++;
        }
        NativeHistoryInstruction Decode(JsonElement instruction) => new(slot, position, sequence++, signature,
            keys[instruction.GetProperty("programIdIndex").GetInt32()], instruction.GetProperty("accounts").EnumerateArray().Select(key => keys[key.GetInt32()]).ToArray(),
            Encoders.Base58.DecodeData(instruction.GetProperty("data").GetString()!));
    }

    private static bool TryNativeCreatedBufferSize(byte[] data, out int size)
    {
        size = 0;
        if (data.Length < 4) return false;
        var discriminator = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var offset = 4;
        if (discriminator == 3)
        {
            if (data.Length < 44) return false;
            var seedLength = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(36));
            if (seedLength > 32) return false;
            offset = 44 + (int)seedLength;
        }
        else if (discriminator != 0) return false;
        if (data.Length != offset + 48 || new PublicKey(data.AsSpan(offset + 16, 32).ToArray()).Key != SolanaNativeLoader.Program.Key) return false;
        var space = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset + 8));
        if (space < SolanaNativeLoader.BufferHeaderSize || space > 10_485_760) return false;
        size = checked((int)space - SolanaNativeLoader.BufferHeaderSize);
        return true;
    }
}
