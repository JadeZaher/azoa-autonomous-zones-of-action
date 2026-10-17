using System.Text.Json;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Exact finalized wire account roles with RPC-reported version-zero loaded addresses.</summary>
internal static class SolanaObservedTransaction
{
    public sealed record Accounts(SolanaMessageEnvelope.Parsed Message, IReadOnlyList<PublicKey> Keys)
    {
        public bool Writable(int index) => index < Message.RequiredSignatures
            ? index < Message.RequiredSignatures - Message.ReadonlySigned
            : index < Message.StaticKeys.Count ? index < Message.StaticKeys.Count - Message.ReadonlyUnsigned
            : index < Message.StaticKeys.Count + Message.Lookups.Sum(lookup => lookup.WritableIndices.Length);
    }

    public static Accounts Read(JsonElement transaction, string signature)
    {
        var encoded = transaction.GetProperty("transaction");
        if (encoded.ValueKind != JsonValueKind.Array || encoded.GetArrayLength() != 2 || encoded[1].GetString() != "base64")
            throw new InvalidDataException("Exact base64 finalized transaction is required.");
        var wire = Convert.FromBase64String(encoded[0].GetString()!);
        if (wire.Length == 0 || wire[0] is 0 or > 127 || wire.Length <= 1 + wire[0] * 64)
            throw new InvalidDataException("Finalized transaction has invalid signature framing.");
        var messageBytes = wire[(1 + wire[0] * 64)..];
        if (SolanaMessageEnvelope.VerifyWire(wire, messageBytes) != signature)
            throw new InvalidDataException("Finalized transaction signature differs from its exact wire.");
        var message = SolanaMessageEnvelope.Read(messageBytes);
        var keys = message.StaticKeys.ToList();
        if (message.VersionZero)
        {
            var loaded = transaction.GetProperty("meta").GetProperty("loadedAddresses");
            var writable = loaded.GetProperty("writable"); var readOnly = loaded.GetProperty("readonly");
            if (writable.ValueKind != JsonValueKind.Array || readOnly.ValueKind != JsonValueKind.Array
                || writable.GetArrayLength() != message.Lookups.Sum(value => value.WritableIndices.Length)
                || readOnly.GetArrayLength() != message.Lookups.Sum(value => value.ReadonlyIndices.Length))
                throw new InvalidDataException("Finalized loaded account roles differ from the signed lookup indices.");
            keys.AddRange(writable.EnumerateArray().Concat(readOnly.EnumerateArray()).Select(value => new PublicKey(value.GetString()!)));
        }
        if (keys.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() != keys.Count)
            throw new InvalidDataException("Finalized transaction contains colliding account roles.");
        return new(message, keys);
    }
}
