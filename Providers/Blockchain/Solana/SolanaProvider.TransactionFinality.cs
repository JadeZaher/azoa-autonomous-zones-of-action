using System.Globalization;
using System.Text.Json;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private Task<JsonElement> ReadSolanaFinalizedTransactionAsync(string signature, CancellationToken ct)
        => DurableRpcAsync("getTransaction", [signature, new { encoding = "base64", commitment = "finalized", maxSupportedTransactionVersion = 0 }], ct);

    private static IReadOnlyList<SolanaLookupTableSnapshot> UsedSolanaLookupTables(byte[] message,
        IReadOnlyList<SolanaLookupTableSnapshot> saved)
        => SolanaMessageEnvelope.Read(message).Lookups.Select(lookup => saved.Single(value => value.Address == lookup.Address)).ToArray();

    private sealed record SolanaFinalized(SolanaObservedTransaction.Accounts Accounts, JsonElement Meta,
        ulong Slot, string BlockIdentity, ulong Fee, bool Failed);

    private async Task<SolanaFinalized> RequireSolanaFinalityAsync(JsonElement transaction, string transactionId,
        string signedBase64, string messageBase64, IReadOnlyList<SolanaLookupTableSnapshot> tables, ulong networkFeeCap, CancellationToken ct)
    {
        var observed = SolanaObservedTransaction.Read(transaction, transactionId);
        if (transaction.GetProperty("transaction")[0].GetString() != signedBase64)
            throw new InvalidDataException("Finalized Solana transaction differs from the saved signed candidate.");
        var slot = transaction.GetProperty("slot").GetUInt64(); var meta = transaction.GetProperty("meta");
        if (!meta.TryGetProperty("fee", out var feeValue) || feeValue.ValueKind != JsonValueKind.Number || !feeValue.TryGetUInt64(out var fee)
            || fee > networkFeeCap || !meta.TryGetProperty("err", out var error))
            throw new InvalidDataException("Exact finalized network fee or transaction status is unavailable.");
        var block = await DurableRpcAsync("getBlock", [slot, new { commitment = "finalized", transactionDetails = "signatures", rewards = false }], ct);
        if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("blockhash", out var blockHash)
            || blockHash.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(blockHash.GetString())
            || !block.TryGetProperty("signatures", out var signatures) || signatures.ValueKind != JsonValueKind.Array
            || signatures.EnumerateArray().Count(value => value.ValueKind == JsonValueKind.String && value.GetString() == transactionId) != 1)
            throw new InvalidDataException("Finalized canonical block does not uniquely contain the saved Solana transaction.");
        var repeated = await ReadSolanaFinalizedTransactionAsync(transactionId, ct);
        if (repeated.ValueKind != JsonValueKind.Object || repeated.GetProperty("slot").GetUInt64() != slot
            || repeated.GetProperty("transaction")[0].GetString() != signedBase64
            || repeated.GetProperty("meta").GetProperty("err").GetRawText() != error.GetRawText()
            || repeated.GetProperty("meta").GetProperty("fee").GetUInt64() != fee)
            throw new InvalidDataException("Finalized Solana transaction changed while collecting evidence.");
        var message = Convert.FromBase64String(messageBase64);
        var expected = SolanaMessageEnvelope.Resolve(SolanaMessageEnvelope.Read(message), UsedSolanaLookupTables(message, tables));
        if (!observed.Keys.Select(value => value.Key).SequenceEqual(expected.Select(value => value.Key)))
            throw new InvalidDataException("Finalized lookup-table resolution differs from the frozen Solana message.");
        return new(observed, meta.Clone(), slot, slot.ToString(CultureInfo.InvariantCulture) + ":" + blockHash.GetString(), fee,
            error.ValueKind != JsonValueKind.Null);
    }
}
