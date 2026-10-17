extern alias BCCrypto2;
using System.Numerics;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using Nethereum.Signer;
using Solnet.Wallet;
using Ed25519Signer = BCCrypto2::Org.BouncyCastle.Crypto.Signers.Ed25519Signer;
using Ed25519PublicKeyParameters = BCCrypto2::Org.BouncyCastle.Crypto.Parameters.Ed25519PublicKeyParameters;

namespace AZOA.WebAPI.Services.Signing;

/// <inheritdoc/>
public sealed class WalletSignatureVerifier : IWalletSignatureVerifier
{
    private static readonly BigInteger Secp256k1Order = new(Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141"), true, true);

    /// <inheritdoc/>
    public bool Verify(string chainType, string address, byte[] message, byte[] signature)
    {
        if (message is not { Length: > 0 } || signature is null
            || !WalletAuthIdentity.TryCreate(address, chainType, out var canonical, out var chain)) return false;
        if (chain == "ethereum") return VerifyEthereum(canonical, message, signature);
        if (signature.Length != 64) return false;
        var publicKey = chain == "algorand" ? new Algorand.Address(canonical).Bytes : new PublicKey(canonical).KeyBytes;
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
        verifier.BlockUpdate(message, 0, message.Length);
        return verifier.VerifySignature(signature);
    }

    private static bool VerifyEthereum(string address, byte[] message, byte[] signature)
    {
        if (signature.Length != 65 || signature[64] is not (0 or 1 or 27 or 28)) return false;
        var r = new BigInteger(signature.AsSpan(0, 32), true, true);
        var s = new BigInteger(signature.AsSpan(32, 32), true, true);
        if (r <= 0 || r >= Secp256k1Order || s <= 0 || s > Secp256k1Order / 2) return false;
        var normalized = signature.ToArray();
        if (normalized[64] < 27) normalized[64] += 27;
        try
        {
            var recovered = new EthereumMessageSigner().EcRecover(message, "0x" + Convert.ToHexString(normalized));
            return EthereumAddress.AreSame(address, recovered);
        }
        catch (ArgumentException) { return false; }
        catch (ArithmeticException) { return false; }
    }
}
