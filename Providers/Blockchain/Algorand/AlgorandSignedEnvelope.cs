extern alias BCCrypto2;
using Algorand;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using Ed25519Signer = BCCrypto2::Org.BouncyCastle.Crypto.Signers.Ed25519Signer;
using Ed25519PublicKeyParameters = BCCrypto2::Org.BouncyCastle.Crypto.Parameters.Ed25519PublicKeyParameters;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

/// <summary>Accepts exactly one canonical signed envelope, excluding hidden trailing MessagePack objects.</summary>
internal static class AlgorandSignedEnvelope
{
    public static SignedTransaction DecodeCanonical(string base64)
    {
        var wire = Convert.FromBase64String(base64);
        var signed = Encoder.DecodeFromMsgPack<SignedTransaction>(wire);
        if (!wire.AsSpan().SequenceEqual(Encoder.EncodeToMsgPackOrdered(signed)))
            throw new InvalidOperationException("Persisted Algorand envelope is noncanonical or contains trailing bytes.");
        return signed;
    }

    /// <summary>
    /// Accepts exactly the canonical envelope of <paramref name="expected"/> signed by <paramref name="sender"/>. The body is
    /// compared by re-encoding the expected transaction, never by trusting the SDK's decode of the wire body (it decodes a
    /// zero-amount axfer as an opt-in and drops its close-to field).
    /// </summary>
    public static SignedTransaction MatchExact(string base64, Transaction expected, string sender)
    {
        var wire = Convert.FromBase64String(base64);
        var decoded = Encoder.DecodeFromMsgPack<SignedTransaction>(wire);
        if (decoded.AuthAddr is not null || decoded.MSig is not null || decoded.LSig is not null || decoded.Sig?.Bytes is not { Length: 64 })
            throw new InvalidOperationException("The saved sender's single-key signature is required.");
        var signed = new SignedTransaction(expected, decoded.Sig);
        if (!wire.AsSpan().SequenceEqual(Encoder.EncodeToMsgPackOrdered(signed)))
            throw new InvalidOperationException("Signed Algorand envelope differs from the saved unsigned transaction or carries trailing bytes.");
        RequireSingleSender(signed, sender);
        return signed;
    }

    public static void RequireSingleSender(SignedTransaction signed, string sender)
    {
        if (signed.AuthAddr is not null || signed.MSig is not null || signed.LSig is not null
            || signed.Sig?.Bytes is not { Length: 64 } signature || signed.Tx.Sender.EncodeAsString() != sender)
            throw new InvalidOperationException("The saved sender's single-key signature is required.");
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(new Address(sender).Bytes, 0));
        var message = signed.Tx.BytesToSign();
        verifier.BlockUpdate(message, 0, message.Length);
        if (!verifier.VerifySignature(signature)) throw new InvalidOperationException("Signature does not authorize the saved sender.");
    }
}
