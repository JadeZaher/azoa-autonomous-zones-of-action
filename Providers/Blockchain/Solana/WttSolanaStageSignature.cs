extern alias BCCrypto2;
using Ed25519PrivateKeyParameters = BCCrypto2::Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters;
using Ed25519Signer = BCCrypto2::Org.BouncyCastle.Crypto.Signers.Ed25519Signer;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Ed25519 message signatures for protected, durable WTT stage seeds.</summary>
internal static class WttSolanaStageSignature
{
    public static string PublicKey(byte[] seed)
    {
        if (seed.Length != 32) throw new ArgumentException("WTT stage seed must contain 32 bytes.", nameof(seed));
        return new PublicKey(new Ed25519PrivateKeyParameters(seed, 0).GeneratePublicKey().GetEncoded()).Key;
    }

    public static byte[] Sign(byte[] seed, string expectedPublicKey, byte[] message)
    {
        if (PublicKey(seed) != expectedPublicKey) throw new InvalidOperationException("Protected WTT stage key differs from the required signer.");
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(seed, 0));
        signer.BlockUpdate(message, 0, message.Length);
        var signature = signer.GenerateSignature();
        if (!new PublicKey(expectedPublicKey).Verify(message, signature))
            throw new InvalidOperationException("WTT stage signature verification failed.");
        return signature;
    }
}
