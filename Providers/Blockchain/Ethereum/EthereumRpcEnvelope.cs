using System.Numerics;
using System.Text.Json;
using Nethereum.Model;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

/// <summary>Reconstructs actual RPC signature bytes for the closed empty-access-list type-2 grammar.</summary>
internal static class EthereumRpcEnvelope
{
    internal static byte[] Read(JsonElement value, byte[] expectedUnsigned, string sender) => Reconstruct(value, expectedUnsigned, sender);

    /// <summary>Reconstructs the sender's own signed bytes for a wallet-chosen envelope (fees, gas) that matches no saved plan.</summary>
    internal static byte[] ReadOwnEnvelope(JsonElement value, string sender) => Reconstruct(value, null, sender);

    private static byte[] Reconstruct(JsonElement value, byte[]? expectedUnsigned, string sender)
    {
        if (Number(value, "type") != 2 || !value.TryGetProperty("accessList", out var access) ||
            access.ValueKind != JsonValueKind.Array || access.GetArrayLength() != 0 ||
            !EthereumAddress.AreSame(Text(value, "from"), sender))
            throw new InvalidDataException("RPC transaction does not match the closed original-owner type-2 grammar.");
        var transaction = new Transaction1559(Number(value, "chainId"), Number(value, "nonce"),
            Number(value, "maxPriorityFeePerGas"), Number(value, "maxFeePerGas"), Number(value, "gas"),
            Text(value, "to"), Number(value, "value"), Text(value, "input"), null);
        var hasParity = value.TryGetProperty("yParity", out _); var hasV = value.TryGetProperty("v", out _);
        if (!hasParity && !hasV) throw new InvalidDataException("RPC transaction omitted its actual recovery parity.");
        var parity = Number(value, hasParity ? "yParity" : "v");
        if (parity > 1 || hasParity && hasV && Number(value, "v") != parity)
            throw new InvalidDataException("RPC transaction carries conflicting recovery parity.");
        transaction.SetSignature(new Signature(Number(value, "r").ToByteArray(true, true), Number(value, "s").ToByteArray(true, true), [(byte)parity]));
        var wire = transaction.GetRLPEncoded();
        var identity = EthereumSignedEnvelope.Verify(wire, expectedUnsigned ?? transaction.GetRLPEncodedRaw(), sender);
        if (!identity.Equals(Text(value, "hash"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("RPC transaction hash differs from its actual signature and complete payload.");
        return wire;
    }

    private static string Text(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) &&
            field.ValueKind == JsonValueKind.String && field.GetString() is { Length: > 0 and <= 65538 } text
            ? text : throw new InvalidDataException("RPC transaction omitted an exact " + name + " field.");

    private static BigInteger Number(JsonElement value, string name)
        => EthereumHex.TryParseQuantity(Text(value, name), out var number) ? number
            : throw new InvalidDataException("RPC transaction carries an invalid " + name + " quantity.");
}
