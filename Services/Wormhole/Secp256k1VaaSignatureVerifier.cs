extern alias BCCrypto2;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using SecNamedCurves = BCCrypto2::Org.BouncyCastle.Asn1.Sec.SecNamedCurves;
using X9ECParameters = BCCrypto2::Org.BouncyCastle.Asn1.X9.X9ECParameters;
using BigInteger = BCCrypto2::Org.BouncyCastle.Math.BigInteger;
using ECPoint = BCCrypto2::Org.BouncyCastle.Math.EC.ECPoint;
using ECCurve = BCCrypto2::Org.BouncyCastle.Math.EC.ECCurve;
using FpCurve = BCCrypto2::Org.BouncyCastle.Math.EC.FpCurve;
using ECAlgorithms = BCCrypto2::Org.BouncyCastle.Math.EC.ECAlgorithms;

namespace AZOA.WebAPI.Services.Wormhole;

/// <inheritdoc/>
public sealed class Secp256k1VaaSignatureVerifier : IVaaSignatureVerifier, IWttGuardianSignatureVerifier
{
    private readonly WormholeConfig _config;
    private readonly ILogger<Secp256k1VaaSignatureVerifier> _logger;

    private static readonly X9ECParameters Secp256k1 = SecNamedCurves.GetByName("secp256k1");
    private static readonly ECCurve Curve = Secp256k1.Curve;
    private static readonly ECPoint G = Secp256k1.G;
    private static readonly BigInteger N = Secp256k1.N;
    private static readonly BigInteger PrimeP =
        ((FpCurve)Curve).Q; // field characteristic p (secp256k1 is over F_p)

    public Secp256k1VaaSignatureVerifier(
        IOptions<WormholeConfig> config,
        ILogger<Secp256k1VaaSignatureVerifier> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<bool> VerifySignatureAsync(
        byte[] digest,
        WormholeVaaSignature signature,
        int guardianSetIndex,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Verify(digest, signature, guardianSetIndex));
    }

    private bool Verify(byte[] digest, WormholeVaaSignature signature, int guardianSetIndex)
    {

        if (digest is null || digest.Length != 32)
            return false;
        if (signature is null)
            return false;
        if (signature.R is null || signature.R.Length != 32)
            return false;
        if (signature.S is null || signature.S.Length != 32)
            return false;
        if (signature.GuardianIndex < 0)
            return false;

        int recId = signature.V;
        if (recId is 27 or 28) recId -= 27;
        if (recId is not (0 or 1))
            return false;

        if (_config.GuardianSets is null ||
            !_config.GuardianSets.TryGetValue(
                guardianSetIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                out var guardianAddresses) ||
            guardianAddresses is null)
        {
            _logger.LogWarning(
                "No configured Guardian set for index {SetIndex} — cannot verify " +
                "(fill Blockchain:Wormhole:GuardianSets for this network)",
                guardianSetIndex);
            return false;
        }

        if (signature.GuardianIndex >= guardianAddresses.Count)
            return false;

        byte[]? expectedAddress = ParseAddress(guardianAddresses[signature.GuardianIndex]);
        if (expectedAddress is null)
            return false;

        return VerifyGuardianSignature(digest, signature, expectedAddress);
    }

    /// <inheritdoc/>
    public bool VerifyGuardianSignature(byte[] digest, WormholeVaaSignature signature, byte[] expectedAddress)
    {
        if (digest is not { Length: 32 } || expectedAddress is not { Length: 20 }
            || signature?.R is not { Length: 32 } || signature.S is not { Length: 32 }) return false;
        int recId = signature.V;
        if (recId is 27 or 28) recId -= 27;
        if (recId is not (0 or 1)) return false;
        var r = new BigInteger(1, signature.R);
        var s = new BigInteger(1, signature.S);
        if (r.SignValue <= 0 || s.SignValue <= 0)
            return false;
        if (r.CompareTo(N) >= 0 || s.CompareTo(N) >= 0)
            return false;

        var recovered = RecoverPublicKey(digest, r, s, recId);
        if (recovered is null)
            return false;

        byte[] encoded = recovered.GetEncoded(false); // 65 bytes: 0x04 || X(32) || Y(32)
        if (encoded.Length != 65 || encoded[0] != 0x04)
            return false;

        var xy = new byte[64];
        System.Array.Copy(encoded, 1, xy, 0, 64);
        byte[] hash = Keccak256.ComputeHash(xy);
        var recoveredAddress = new byte[20];
        System.Array.Copy(hash, 12, recoveredAddress, 0, 20); // last 20 bytes

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(recoveredAddress, expectedAddress);
    }

    private static ECPoint? RecoverPublicKey(byte[] digest, BigInteger r, BigInteger s, int recId)
    {
        // SEC1 4.1.6 public-key recovery using the vetted Bouncy Castle curve.
        var e = new BigInteger(1, digest);

        BigInteger x = r;
        if (x.CompareTo(PrimeP) >= 0)
            return null;

        ECPoint? bigR = DecompressPoint(x, (recId & 1) == 1);
        if (bigR is null || !bigR.IsValid())
            return null;

        if (!bigR.Multiply(N).IsInfinity)
            return null;

        BigInteger rInv = r.ModInverse(N);
        BigInteger srInv = s.Multiply(rInv).Mod(N);
        BigInteger erInv = e.Multiply(rInv).Mod(N);

        ECPoint q = ECAlgorithms.SumOfTwoMultiplies(bigR, srInv, G, erInv.Negate().Mod(N));
        q = q.Normalize();

        if (q.IsInfinity || !q.IsValid())
            return null;

        return q;
    }

    private static ECPoint? DecompressPoint(BigInteger x, bool yOdd)
    {
        try
        {

            var compressed = new byte[33];
            compressed[0] = (byte)(yOdd ? 0x03 : 0x02);
            byte[] xb = BigIntegerTo32Bytes(x);
            System.Array.Copy(xb, 0, compressed, 1, 32);
            ECPoint p = Curve.DecodePoint(compressed);
            return p.IsValid() ? p : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static byte[] BigIntegerTo32Bytes(BigInteger v)
    {
        byte[] raw = v.ToByteArrayUnsigned(); // no sign byte, big-endian
        if (raw.Length == 32) return raw;
        if (raw.Length > 32)
            throw new ArgumentException("x coordinate exceeds 32 bytes");
        var padded = new byte[32];
        System.Array.Copy(raw, 0, padded, 32 - raw.Length, raw.Length);
        return padded;
    }

    private static byte[]? ParseAddress(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;
        var h = hex.Trim();
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            h = h[2..];
        if (h.Length != 40)
            return null;
        try
        {
            return Convert.FromHexString(h);
        }
        catch (FormatException)
        {
            return null;
        }
    }

}
