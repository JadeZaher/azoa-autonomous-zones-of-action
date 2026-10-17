extern alias BCCrypto2;

using Nethereum.Model;
using Nethereum.Signer;
using Nethereum.Util;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using BcBigInteger = BCCrypto2::Org.BouncyCastle.Math.BigInteger;
using SecNamedCurves = BCCrypto2::Org.BouncyCastle.Asn1.Sec.SecNamedCurves;
using ECDomainParameters = BCCrypto2::Org.BouncyCastle.Crypto.Parameters.ECDomainParameters;
using ECPrivateKeyParameters = BCCrypto2::Org.BouncyCastle.Crypto.Parameters.ECPrivateKeyParameters;
using ECDsaSigner = BCCrypto2::Org.BouncyCastle.Crypto.Signers.ECDsaSigner;
using HMacDsaKCalculator = BCCrypto2::Org.BouncyCastle.Crypto.Signers.HMacDsaKCalculator;
using Sha512Digest = BCCrypto2::Org.BouncyCastle.Crypto.Digests.Sha512Digest;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>Produces a second deterministic low-S signature over unchanged type-2 signing bytes.</summary>
internal static class EthereumAlternateSignature
{
    internal static byte[] Sign(byte[] unsigned, EthECKey key)
    {
        var curve = SecNamedCurves.GetByName("secp256k1");
        var domain = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        // RFC6979 uses a different nonce derivation hash; the signed Keccak digest is unchanged.
        var signer = new ECDsaSigner(new HMacDsaKCalculator(new Sha512Digest()));
        signer.Init(true, new ECPrivateKeyParameters(new BcBigInteger(1, key.GetPrivateKeyAsBytes()), domain));
        var scalars = signer.GenerateSignature(new Sha3Keccack().CalculateHash(unsigned));
        var s = scalars[1].CompareTo(curve.N.ShiftRight(1)) > 0 ? curve.N.Subtract(scalars[1]) : scalars[1];
        var transaction = Transaction1559Encoder.Current.Decode(unsigned);
        for (byte parity = 0; parity < 2; parity++)
        {
            transaction.SetSignature(new Signature(scalars[0].ToByteArrayUnsigned(), s.ToByteArrayUnsigned(), [parity]));
            if (EthereumAddress.AreSame(TransactionVerificationAndRecovery.GetSenderAddress(transaction), key.GetPublicAddress()))
                return transaction.GetRLPEncoded();
        }
        throw new InvalidOperationException("Fixture could not recover its original signer.");
    }
}
