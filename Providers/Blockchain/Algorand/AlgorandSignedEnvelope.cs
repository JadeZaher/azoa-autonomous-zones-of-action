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
