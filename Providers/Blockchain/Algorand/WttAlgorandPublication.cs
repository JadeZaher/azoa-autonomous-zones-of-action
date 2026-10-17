using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

/// <summary>Extracts the exact core publication from the accepted token-bridge call's inner execution tree.</summary>
internal static class WttAlgorandPublication
{
    internal static WttPublication? Read(JsonElement outer, WttOperationIntent intent, string stage,
        string transactionId, string blockIdentity, uint timestamp)
    {
        if (stage is not ("Attest" or "Transfer") || !Text(outer, "id", transactionId)
            || !Object(outer, "application-transaction", out var application)
            || !Number(application, "application-id", out var token)
            || token.ToString(CultureInfo.InvariantCulture) != intent.Source.TokenBridge) return null;
        var payload = stage == "Attest"
            ? WttPayloadCodec.EncodeAttestation(new(intent.Asset.OriginAddress, 8, (byte)Math.Min((int)intent.Asset.Decimals, 8),
                intent.Asset.SymbolHex, intent.Asset.NameHex))
            : WttPayloadCodec.EncodeTransfer(new(intent.Amount.Amount, intent.Asset.OriginAddress, intent.Asset.OriginChain,
                intent.RecipientProtocolAddress, intent.Target.ProtocolChain, intent.Amount.Fee));
        var found = new List<(string Path, ulong Sequence)>();
        var count = 0;
        if (!Visit(outer, "", 0) || found.Count != 1) return null;
        var selected = found[0];
        var emitter = global::Algorand.Address.ForApplication(token).Bytes;
        var body = new byte[51 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(body, timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(8), 8);
        emitter.CopyTo(body, 10);
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(42), selected.Sequence);
        payload.CopyTo(body, 51);
        return new(intent.Source.ProtocolIdentity, intent.Source.CoreBridge, intent.Source.TokenBridge,
            transactionId, blockIdentity, selected.Path, timestamp, 0, 8, Convert.ToHexStringLower(emitter),
            selected.Sequence.ToString(CultureInfo.InvariantCulture), 0, Convert.ToHexStringLower(payload),
            Convert.ToHexStringLower(body), Convert.ToHexStringLower(Keccak256.ComputeHash(Keccak256.ComputeHash(body))));

        bool Visit(JsonElement parent, string path, int depth)
        {
            if (!parent.TryGetProperty("inner-txns", out var inner)) return true;
            if (inner.ValueKind != JsonValueKind.Array || depth >= 16) return false;
            var index = 0;
            foreach (var child in inner.EnumerateArray())
            {
                if (++count > 1024 || child.ValueKind != JsonValueKind.Object) return false;
                var location = path.Length == 0 ? index.ToString(CultureInfo.InvariantCulture) : path + "/" + index.ToString(CultureInfo.InvariantCulture);
                index++;
                if (child.TryGetProperty("application-transaction", out var call)
                    && Number(call, "application-id", out var core)
                    && core.ToString(CultureInfo.InvariantCulture) == intent.Source.CoreBridge)
                {
                    if (!Text(child, "sender", global::Algorand.Address.ForApplication(token).ToString())
                        || !Text(child, "tx-type", "appl")
                        || !Text(call, "on-completion", "noop")
                        || !Array(call, "application-args", out var args) || args.GetArrayLength() != 3
                        || !Binary(args[0], "publishMessage"u8) || !Binary(args[1], payload) || !Binary(args[2], new byte[8])
                        || !Array(child, "logs", out var logs) || logs.GetArrayLength() != 1
                        || logs[0].ValueKind != JsonValueKind.String) return false;
                    byte[] sequence;
                    try { sequence = Convert.FromBase64String(logs[0].GetString()!); }
                    catch (FormatException) { return false; }
                    if (sequence.Length != 8 || Convert.ToBase64String(sequence) != logs[0].GetString()) return false;
                    found.Add((location, BinaryPrimitives.ReadUInt64BigEndian(sequence)));
                }
                if (!Visit(child, location, depth + 1)) return false;
            }
            return true;
        }
    }

    private static bool Binary(JsonElement value, ReadOnlySpan<byte> expected)
        => value.ValueKind == JsonValueKind.String && value.GetString() == Convert.ToBase64String(expected);

    private static bool Number(JsonElement value, string name, out ulong number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
            && field.ValueKind == JsonValueKind.Number && field.TryGetUInt64(out number);
    }

    private static bool Text(JsonElement value, string name, string expected)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
            && field.ValueKind == JsonValueKind.String && field.GetString() == expected;

    private static bool Object(JsonElement value, string name, out JsonElement field)
    {
        field = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out field) && field.ValueKind == JsonValueKind.Object;
    }

    private static bool Array(JsonElement value, string name, out JsonElement field)
    {
        field = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out field) && field.ValueKind == JsonValueKind.Array;
    }
}
