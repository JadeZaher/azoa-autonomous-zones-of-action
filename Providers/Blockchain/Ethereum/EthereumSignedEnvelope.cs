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
        var transaction = Transaction1559Encoder.Current.Decode(wire);
        if (transaction is null || !transaction.GetRLPEncoded().AsSpan().SequenceEqual(wire) ||
            !transaction.GetRLPEncodedRaw().AsSpan().SequenceEqual(expectedUnsigned) || transaction.Signature is not { } signature ||
            signature.R is not { Length: > 0 and <= 32 } || signature.S is not { Length: > 0 and <= 32 } ||
            signature.V is not { Length: <= 1 })
            throw new InvalidDataException("Ethereum wire changes the exact canonical transaction or signature framing.");
        if (!EthereumSignatureRules.IsCanonical(signature.R, signature.S, signature.V.Length == 0 ? (byte)0 : signature.V[0]) ||
            !TransactionVerificationAndRecovery.VerifyTransaction(transaction) ||
            !EthereumAddress.AreSame(TransactionVerificationAndRecovery.GetSenderAddress(transaction), sender))
            throw new InvalidDataException("Ethereum wire has no valid canonical signature from the original sender.");
        return EthereumHex.Data(new Sha3Keccack().CalculateHash(wire));
    }
}
