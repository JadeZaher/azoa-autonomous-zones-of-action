// Bind ALL Bouncy Castle types in this file to the vetted 2.x assembly
// (BouncyCastle.Cryptography) via its csproj alias, never the legacy 1.8.8
// BouncyCastle.Crypto transitively bundled by the Algorand2/Solana SDKs.
extern alias BCCrypto2;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using SecNamedCurves = BCCrypto2::Org.BouncyCastle.Asn1.Sec.SecNamedCurves;
using X9ECParameters = BCCrypto2::Org.BouncyCastle.Asn1.X9.X9ECParameters;
using BigInteger = BCCrypto2::Org.BouncyCastle.Math.BigInteger;
using ECPoint = BCCrypto2::Org.BouncyCastle.Math.EC.ECPoint;
using ECCurve = BCCrypto2::Org.BouncyCastle.Math.EC.ECCurve;
using FpCurve = BCCrypto2::Org.BouncyCastle.Math.EC.FpCurve;
using ECAlgorithms = BCCrypto2::Org.BouncyCastle.Math.EC.ECAlgorithms;

namespace AZOA.WebAPI.Services.Wormhole;

/// <summary>
/// Real per-signature Wormhole VAA Guardian verification.
///
/// <para>Performs standard SEC1 ECDSA public-key recovery on curve
/// <c>secp256k1</c> (the curve Ethereum and the Wormhole Guardians use) over
/// the 32-byte canonical VAA signing digest, then derives the Ethereum-style
/// Guardian address from the recovered public key and confirms it equals the
/// expected Guardian for <c>(guardianSetIndex, signature.GuardianIndex)</c>
/// from config.</para>
///
/// <para><b>Crypto provenance:</b> all curve / point arithmetic and the modular
/// big-integer math are delegated to the vetted Bouncy Castle library
/// (<c>Org.BouncyCastle</c>, official package
/// <c>BouncyCastle.Cryptography</c>). This class only wires the standard public
/// recovery algorithm (SEC1 §4.1.6) around that vetted arithmetic — no homebake
/// curve math. Keccak-256 is the project's existing pure-managed
/// <see cref="Keccak256"/> (no second hashing dependency).</para>
///
/// <para><b>Division of responsibility:</b> this verifier performs ONLY
/// per-signature recovery + Guardian-membership. Signature count, the
/// Byzantine quorum (floor(2/3·N)+1), strictly-ascending/unique Guardian
/// indices and structural parsing are owned by <c>WormholeAdapter</c> and are
/// intentionally NOT duplicated here.</para>
///
/// <para><b>Fail-closed:</b> any malformed input, out-of-range value, missing
/// config, recovery failure, or unexpected exception yields
/// <c>false</c> — never an exception that a caller could mistake for "valid".
/// The digest is consumed AS-IS (it is already
/// <c>keccak256(keccak256(body))</c>); it is NOT re-hashed.</para>
/// </summary>
public sealed class Secp256k1VaaSignatureVerifier : IVaaSignatureVerifier
{
    private readonly WormholeConfig _config;
    private readonly ILogger<Secp256k1VaaSignatureVerifier> _logger;

    // secp256k1 domain parameters, sourced from the vetted Bouncy Castle
    // named-curve table (X9.62 / SEC2 "secp256k1"). Curve order n, generator G
    // and the curve itself all come from the library — we never hand-encode
    // these constants.
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

    /// <inheritdoc />
    public Task<bool> VerifySignatureAsync(
        byte[] digest,
        WormholeVaaSignature signature,
        int guardianSetIndex,
        CancellationToken ct = default)
    {
        try
        {
            return Task.FromResult(Verify(digest, signature, guardianSetIndex));
        }
        catch (Exception ex)
        {
            // Fail-closed: a thrown verifier is treated as "not valid", never
            // as success. (The adapter also catches, but defence in depth.)
            _logger.LogWarning(ex,
                "VAA signature verification threw for guardian {GuardianIndex} " +
                "in set {SetIndex} — treating as invalid (fail-closed)",
                signature?.GuardianIndex, guardianSetIndex);
            return Task.FromResult(false);
        }
    }

    private bool Verify(byte[] digest, WormholeVaaSignature signature, int guardianSetIndex)
    {
        // ─── 0. Input sanity (fail-closed on anything malformed) ───
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

        // Recovery id must be 0 or 1. Wormhole stores the raw {0,1} id (some
        // wire formats use 27/28 — normalise that defensively, still rejecting
        // anything outside {0,1} after normalisation).
        int recId = signature.V;
        if (recId is 27 or 28) recId -= 27;
        if (recId is not (0 or 1))
            return false;

        // ─── 1. Resolve the expected Guardian address from config ───
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

        // ─── 2. Reject malformed r/s (zero or >= curve order n) ───
        var r = new BigInteger(1, signature.R);
        var s = new BigInteger(1, signature.S);
        if (r.SignValue <= 0 || s.SignValue <= 0)
            return false;
        if (r.CompareTo(N) >= 0 || s.CompareTo(N) >= 0)
            return false;

        // ─── 3. SEC1 §4.1.6 public-key recovery on secp256k1 ───
        var recovered = RecoverPublicKey(digest, r, s, recId);
        if (recovered is null)
            return false;

        // ─── 4. Derive the Ethereum-style Guardian address ───
        // address = last 20 bytes of keccak256( uncompressed pubkey[1..65] )
        // i.e. drop the 0x04 SEC1 prefix, hash the 64-byte X||Y.
        byte[] encoded = recovered.GetEncoded(false); // 65 bytes: 0x04 || X(32) || Y(32)
        if (encoded.Length != 65 || encoded[0] != 0x04)
            return false;

        var xy = new byte[64];
        System.Array.Copy(encoded, 1, xy, 0, 64);
        byte[] hash = Keccak256.ComputeHash(xy);
        var recoveredAddress = new byte[20];
        System.Array.Copy(hash, 12, recoveredAddress, 0, 20); // last 20 bytes

        // ─── 5. Constant-time compare against the expected Guardian ───
        return FixedTimeEquals(recoveredAddress, expectedAddress);
    }

    /// <summary>
    /// Standard SEC1 (§4.1.6) ECDSA public-key recovery for secp256k1.
    /// Curve/point arithmetic is the vetted Bouncy Castle implementation; this
    /// only sequences the recovery steps. <paramref name="recId"/> ∈ {0,1}:
    /// bit 0 selects the candidate point's y parity; the (very rare)
    /// x ≥ p wrap-around case (recId would need an added n to x) is correctly
    /// rejected because Wormhole only ever emits recId ∈ {0,1}.
    /// </summary>
    private static ECPoint? RecoverPublicKey(byte[] digest, BigInteger r, BigInteger s, int recId)
    {
        // e = the 32-byte digest interpreted as a big-endian integer (the
        // digest is consumed as-is — already keccak256(keccak256(body))).
        var e = new BigInteger(1, digest);

        // x = r  (recId bit 1 would mean x = r + n, which only occurs when
        // r + n < p; Wormhole/Ethereum signatures never set that bit, so a
        // recId of strictly {0,1} maps to x = r. If x >= p the point is
        // invalid and decompression below fails ⇒ null ⇒ false.)
        BigInteger x = r;
        if (x.CompareTo(PrimeP) >= 0)
            return null;

        // Decompress R = (x, y) with y-parity = recId's low bit.
        ECPoint? bigR = DecompressPoint(x, (recId & 1) == 1);
        if (bigR is null || !bigR.IsValid())
            return null;

        // n * R must be the point at infinity for a well-formed R.
        if (!bigR.Multiply(N).IsInfinity)
            return null;

        // Q = r^-1 (s*R - e*G)
        BigInteger rInv = r.ModInverse(N);
        BigInteger srInv = s.Multiply(rInv).Mod(N);
        BigInteger erInv = e.Multiply(rInv).Mod(N);

        // Q = (srInv * R) + (-erInv * G)
        ECPoint q = ECAlgorithms.SumOfTwoMultiplies(bigR, srInv, G, erInv.Negate().Mod(N));
        q = q.Normalize();

        if (q.IsInfinity || !q.IsValid())
            return null;

        return q;
    }

    /// <summary>
    /// Decompress an secp256k1 point from its x coordinate and desired
    /// y-parity using Bouncy Castle's curve point factory (no homebake sqrt).
    /// </summary>
    private static ECPoint? DecompressPoint(BigInteger x, bool yOdd)
    {
        try
        {
            // SEC1 compressed point encoding: 0x02 (even y) / 0x03 (odd y)
            // followed by the 32-byte big-endian x. Bouncy Castle performs the
            // modular square root and parity selection internally.
            var compressed = new byte[33];
            compressed[0] = (byte)(yOdd ? 0x03 : 0x02);
            byte[] xb = BigIntegerTo32Bytes(x);
            System.Array.Copy(xb, 0, compressed, 1, 32);
            ECPoint p = Curve.DecodePoint(compressed);
            return p.IsValid() ? p : null;
        }
        catch
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

    /// <summary>
    /// Parse a 0x-prefixed 20-byte hex Guardian address. Returns null for any
    /// malformed/placeholder value (fail-closed — an unfilled mainnet/testnet
    /// placeholder can never accidentally match a recovered address).
    /// </summary>
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
        catch
        {
            return null;
        }
    }

    /// <summary>Constant-time 20-byte address comparison.</summary>
    private static bool FixedTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
            return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}
