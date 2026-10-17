using System.Text.Json;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private async Task<IReadOnlyList<SolanaLookupTableSnapshot>> ReadLookupTablesAsync(IReadOnlyList<string> addresses,
        ulong minimumSlot, CancellationToken ct)
    {
        if (addresses.Count == 0) return [];
        if (addresses.Count > 32 || addresses.Distinct(StringComparer.Ordinal).Count() != addresses.Count
            || addresses.Any(address => new PublicKey(address).Key != address))
            throw new InvalidDataException("Lookup table addresses must be distinct canonical keys within the account bound.");
        var response = await DurableRpcAsync("getMultipleAccounts", [addresses,
            new { encoding = "base64", commitment = "finalized", minContextSlot = minimumSlot }], ct);
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("context", out var context)
            || context.ValueKind != JsonValueKind.Object || !context.TryGetProperty("slot", out var slotValue)
            || slotValue.ValueKind != JsonValueKind.Number || !slotValue.TryGetUInt64(out var slot) || slot < minimumSlot
            || !response.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != addresses.Count)
            throw new InvalidDataException("Finalized lookup table snapshot is malformed or older than the quote.");
        var result = new List<SolanaLookupTableSnapshot>();
        for (var index = 0; index < addresses.Count; index++)
        {
            var bytes = SolanaAccountData.ReadOwned(values[index], SolanaLookupTableSnapshot.Program, false);
            var snapshot = new SolanaLookupTableSnapshot(addresses[index], SolanaLookupTableSnapshot.Program, Convert.ToBase64String(bytes), slot);
            _ = snapshot.ReadActiveAddresses(); result.Add(snapshot);
        }
        return result;
    }

    private async Task RequireCurrentLookupResolutionAsync(byte[] messageBytes,
        IReadOnlyList<SolanaLookupTableSnapshot> saved, CancellationToken ct)
    {
        var message = SolanaMessageEnvelope.Read(messageBytes);
        var original = SolanaMessageEnvelope.Resolve(message, saved);
        if (saved.Count == 0) return;
        var current = await ReadLookupTablesAsync(saved.Select(value => value.Address).ToArray(), saved.Max(value => value.Slot), ct);
        if (!SolanaMessageEnvelope.Resolve(message, current).Select(value => value.Key).SequenceEqual(original.Select(value => value.Key)))
            throw new InvalidDataException("Current lookup resolution differs from the frozen prepared accounts.");
    }
}
