using Nethereum.Model;
using Nethereum.Signer;
using Nethereum.Util;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

/// <summary>Validates one complete canonical type-2 envelope against exact signing bytes and sender.</summary>
internal static class EthereumSignedEnvelope
{
    internal static string Verify(byte[] wire, byte[] expectedUnsigned, string sender)
    {
        if (wire is not { Length: > 1 and <= 32768 } || wire[0] != 2 ||
            expectedUnsigned is not { Length: > 1 and <= 32768 } || expectedUnsigned[0] != 2 || !EthereumAddress.IsNonZero(sender))
            throw new InvalidDataException("An exact bounded type-2 envelope and original sender are required.");
        var transaction = Decode(wire);
        // Framing is checked before re-encoding: Nethereum's encoder dereferences a zero-length scalar.
        if (transaction?.Signature is not { } signature ||
            signature.R is not { Length: > 0 and <= 32 } || signature.S is not { Length: > 0 and <= 32 } ||
            signature.V is not { Length: <= 1 } || !transaction.GetRLPEncoded().AsSpan().SequenceEqual(wire) ||
            !transaction.GetRLPEncodedRaw().AsSpan().SequenceEqual(expectedUnsigned))
            throw new InvalidDataException("Ethereum wire changes the exact canonical transaction or signature framing.");
        if (!EthereumSignatureRules.IsCanonical(signature.R, signature.S, signature.V.Length == 0 ? (byte)0 : signature.V[0]) ||
            !TransactionVerificationAndRecovery.VerifyTransaction(transaction) ||
            !EthereumAddress.AreSame(TransactionVerificationAndRecovery.GetSenderAddress(transaction), sender))
            throw new InvalidDataException("Ethereum wire has no valid canonical signature from the original sender.");
        return EthereumHex.Data(new Sha3Keccack().CalculateHash(wire));
    }

    /// <summary>Decodes caller-supplied bytes, translating the decoder's malformed-input failures to <see cref="InvalidDataException"/>.</summary>
    private static Transaction1559? Decode(byte[] wire)
    {
        try { return Transaction1559Encoder.Current.Decode(wire); }
        catch (Exception exception) when (exception is ArgumentException or IndexOutOfRangeException or InvalidOperationException
            or FormatException or OverflowException or InvalidCastException or NullReferenceException)
        { throw new InvalidDataException("Ethereum wire is not a decodable type-2 envelope.", exception); }
    }
}
