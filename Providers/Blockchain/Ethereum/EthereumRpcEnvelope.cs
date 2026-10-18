using System.Numerics;
using System.Text.Json;
using Nethereum.Model;
using Nethereum.Signer;
using Nethereum.Util;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

/// <summary>Reconstructs actual RPC signature bytes: the closed type-2 grammar for saved plans, any EIP-155 envelope for the owner's own.</summary>
internal static class EthereumRpcEnvelope
{
    internal static byte[] Read(JsonElement value, byte[] expectedUnsigned, string sender) => Reconstruct(value, expectedUnsigned, sender);

    /// <summary>
    /// Reconstructs the sender's own signed bytes for a wallet-chosen envelope (fees, gas, type) that matches no saved plan:
    /// type 2 or type 1 with any access list, or a chain-bound (EIP-155) legacy transaction. F-M13.
    /// </summary>
    internal static byte[] ReadOwnEnvelope(JsonElement value, string sender)
    {
        var type = value.TryGetProperty("type", out _) ? Number(value, "type") : BigInteger.Zero;
        if (type == 2 && value.TryGetProperty("accessList", out var access) && access.ValueKind == JsonValueKind.Array
            && access.GetArrayLength() == 0) return Reconstruct(value, null, sender);
        if (!EthereumAddress.AreSame(Text(value, "from"), sender))
            throw new InvalidDataException("RPC transaction is not from the original owner.");
        var chainId = Number(value, "chainId");
        var r = Number(value, "r").ToByteArray(true, true);
        var s = Number(value, "s").ToByteArray(true, true);
        BigInteger parity;
        ISignedTransaction transaction;
        if (type is { IsZero: true })
        {
            // Pre-EIP-155 (v = 27/28) signatures are not chain-bound and are refused.
            var v = Number(value, "v");
            parity = v - (chainId * 2 + 35);
            if (parity < 0 || parity > 1) throw new InvalidDataException("Legacy RPC transaction is not EIP-155 bound to this chain.");
            transaction = new LegacyTransactionChainId(Text(value, "to"), Number(value, "value"), Number(value, "nonce"),
                Number(value, "gasPrice"), Number(value, "gas"), Text(value, "input"), chainId, r, s, v.ToByteArray(true, true));
        }
        else
        {
            parity = Number(value, value.TryGetProperty("yParity", out _) ? "yParity" : "v");
            if (parity < 0 || parity > 1) throw new InvalidDataException("RPC transaction carries an invalid recovery parity.");
            var signature = new Signature(r, s, [(byte)parity]);
            transaction = type == 1
                ? new Transaction2930(chainId, Number(value, "nonce"), Number(value, "gasPrice"), Number(value, "gas"), Text(value, "to"),
                    Number(value, "value"), Text(value, "input"), AccessList(value), signature)
                : type == 2
                    ? new Transaction1559(chainId, Number(value, "nonce"), Number(value, "maxPriorityFeePerGas"), Number(value, "maxFeePerGas"),
                        Number(value, "gas"), Text(value, "to"), Number(value, "value"), Text(value, "input"), AccessList(value), signature)
                    : throw new InvalidDataException("RPC transaction type is outside the reviewed envelope set.");
        }
        if (!EthereumSignatureRules.IsCanonical(r, s, (byte)parity))
            throw new InvalidDataException("RPC transaction signature is not canonical.");
        var wire = transaction.GetRLPEncoded();
        if (!EthereumHex.Data(new Sha3Keccack().CalculateHash(wire)).Equals(Text(value, "hash"), StringComparison.OrdinalIgnoreCase)
            || !EthereumAddress.AreSame(TransactionVerificationAndRecovery.GetSenderAddress(transaction), sender))
            throw new InvalidDataException("RPC transaction hash or signer differs from its actual signed payload.");
        return wire;
    }

    private static List<AccessListItem> AccessList(JsonElement value)
    {
        if (!value.TryGetProperty("accessList", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("RPC typed transaction omitted its access list.");
        return list.EnumerateArray().Select(item => new AccessListItem(Text(item, "address"),
            item.TryGetProperty("storageKeys", out var keys) && keys.ValueKind == JsonValueKind.Array
                ? keys.EnumerateArray().Select(key => EthereumHex.TryParseData(key.GetString() ?? "", out var bytes) && bytes.Length == 32
                    ? bytes : throw new InvalidDataException("RPC access list carries an invalid storage key.")).ToList()
                : throw new InvalidDataException("RPC access list item omitted its storage keys."))).ToList();
    }

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
