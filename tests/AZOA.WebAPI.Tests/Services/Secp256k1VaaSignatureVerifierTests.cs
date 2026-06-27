// Bind ALL Bouncy Castle types in this test to the vetted 2.x assembly
// (same alias the production verifier uses) so signing here exercises the
// exact curve the verifier recovers on, never the legacy 1.8.8 transitive.
extern alias BCCrypto2;
using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Wormhole;
using BcBigInteger = BCCrypto2::Org.BouncyCastle.Math.BigInteger;
using SecNamedCurves = BCCrypto2::Org.BouncyCastle.Asn1.Sec.SecNamedCurves;
using ECDomainParameters = BCCrypto2::Org.BouncyCastle.Crypto.Parameters.ECDomainParameters;
using ECPrivateKeyParameters = BCCrypto2::Org.BouncyCastle.Crypto.Parameters.ECPrivateKeyParameters;
using ECDsaSigner = BCCrypto2::Org.BouncyCastle.Crypto.Signers.ECDsaSigner;
using HMacDsaKCalculator = BCCrypto2::Org.BouncyCastle.Crypto.Signers.HMacDsaKCalculator;
using Sha256Digest = BCCrypto2::Org.BouncyCastle.Crypto.Digests.Sha256Digest;
using ECAlgorithms = BCCrypto2::Org.BouncyCastle.Math.EC.ECAlgorithms;
using BcECPoint = BCCrypto2::Org.BouncyCastle.Math.EC.ECPoint;

namespace AZOA.WebAPI.Tests.Services;

/// <summary>
/// Crypto-correctness tests for <see cref="Secp256k1VaaSignatureVerifier"/> and
/// its integration with <see cref="WormholeAdapter.VerifyVAAAsync"/>.
///
/// Self-contained: a real secp256k1 keypair is generated in-test (Bouncy
/// Castle), a synthetic Guardian set containing that address is configured, a
/// VAA body is built in the EXACT wire format <c>WormholeAdapter.ParseVAA</c>
/// consumes, the canonical <c>keccak256(keccak256(body))</c> digest is computed
/// the same way the adapter does, and the digest is signed with the correct
/// recovery id. No live Wormhole / network.
/// </summary>
public class Secp256k1VaaSignatureVerifierTests
{
    private static readonly Secp256k1Curve Curve = new();

    // ─── secp256k1 signing helper (test side) ───

    /// <summary>secp256k1 domain + signing/recovery just for the tests.</summary>
    private sealed class Secp256k1Curve
    {
        private readonly BCCrypto2::Org.BouncyCastle.Asn1.X9.X9ECParameters _p =
            SecNamedCurves.GetByName("secp256k1");
        public readonly ECDomainParameters Domain;
        public BcBigInteger N => _p.N;

        public Secp256k1Curve()
        {
            Domain = new ECDomainParameters(_p.Curve, _p.G, _p.N, _p.H);
        }

        /// <summary>Generate a deterministic keypair from a 32-byte private scalar.</summary>
        public (BcBigInteger priv, byte[] address) FromPrivateKey(byte[] priv32)
        {
            var d = new BcBigInteger(1, priv32);
            BcECPoint q = Domain.G.Multiply(d).Normalize();
            byte[] enc = q.GetEncoded(false); // 0x04 || X || Y
            var xy = new byte[64];
            Array.Copy(enc, 1, xy, 0, 64);
            byte[] h = Keccak256.ComputeHash(xy);
            var addr = new byte[20];
            Array.Copy(h, 12, addr, 0, 20);
            return (d, addr);
        }

        public (byte[] r, byte[] s, byte v) Sign(byte[] digest32, BcBigInteger priv)
        {
            // RFC6979 deterministic ECDSA over secp256k1. The digest is signed
            // AS-IS (no extra hashing) — it is already keccak256(keccak256(body)).
            var signer = new ECDsaSigner(new HMacDsaKCalculator(new Sha256Digest()));
            signer.Init(true, new ECPrivateKeyParameters(priv, Domain));
            BcBigInteger[] rs = signer.GenerateSignature(digest32);
            BcBigInteger r = rs[0];
            BcBigInteger s = rs[1];

            // Low-S normalisation (Wormhole/Ethereum convention).
            BcBigInteger halfN = N.ShiftRight(1);
            if (s.CompareTo(halfN) > 0)
                s = N.Subtract(s);

            byte[] rb = To32(r);
            byte[] sb = To32(s);

            // Determine the recovery id by trying both candidates and matching
            // the signer's public key (exactly how a wallet derives v).
            BcECPoint expectedQ = Domain.G.Multiply(priv).Normalize();
            for (byte v = 0; v < 2; v++)
            {
                BcECPoint? rec = Recover(digest32, r, s, v);
                if (rec != null && rec.Equals(expectedQ))
                    return (rb, sb, v);
            }
            throw new InvalidOperationException("could not determine recovery id");
        }

        private BcECPoint? Recover(byte[] digest, BcBigInteger r, BcBigInteger s, int recId)
        {
            var e = new BcBigInteger(1, digest);
            BcBigInteger x = r;
            var fp = (BCCrypto2::Org.BouncyCastle.Math.EC.FpCurve)Domain.Curve;
            if (x.CompareTo(fp.Q) >= 0) return null;

            var comp = new byte[33];
            comp[0] = (byte)((recId & 1) == 1 ? 0x03 : 0x02);
            byte[] xb = To32(x);
            Array.Copy(xb, 0, comp, 1, 32);
            BcECPoint bigR;
            try { bigR = Domain.Curve.DecodePoint(comp); }
            catch { return null; }
            if (!bigR.Multiply(N).IsInfinity) return null;

            BcBigInteger rInv = r.ModInverse(N);
            BcBigInteger srInv = s.Multiply(rInv).Mod(N);
            BcBigInteger erInv = e.Multiply(rInv).Mod(N);
            BcECPoint q = ECAlgorithms.SumOfTwoMultiplies(
                bigR, srInv, Domain.G, erInv.Negate().Mod(N)).Normalize();
            return q.IsInfinity ? null : q;
        }

        private static byte[] To32(BcBigInteger v)
        {
            byte[] raw = v.ToByteArrayUnsigned();
            if (raw.Length == 32) return raw;
            var p = new byte[32];
            Array.Copy(raw, 0, p, 32 - raw.Length, Math.Min(raw.Length, 32));
            return p;
        }
    }

    // ─── VAA wire builder — MUST mirror WormholeAdapter.ParseVAA exactly ───

    private sealed record WireSig(int Index, byte[] R, byte[] S, byte V);

    private static byte[] U32BE(uint v) =>
        new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    private static byte[] U16BE(int v) =>
        new[] { (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF) };
    private static byte[] U64BE(ulong v)
    {
        var b = new byte[8];
        for (int i = 7; i >= 0; i--) { b[i] = (byte)(v & 0xFF); v >>= 8; }
        return b;
    }

    /// <summary>Body bytes EXACTLY as ParseVAA delimits the signed region.</summary>
    private static byte[] BuildBody(
        uint timestamp = 1_700_000_000, uint nonce = 7, int emitterChain = 1,
        byte[]? emitter = null, ulong sequence = 42, byte consistency = 1,
        byte[]? payload = null)
    {
        emitter ??= Emitter();
        payload ??= new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var b = new List<byte>();
        b.AddRange(U32BE(timestamp));
        b.AddRange(U32BE(nonce));
        b.AddRange(U16BE(emitterChain));
        b.AddRange(emitter);
        b.AddRange(U64BE(sequence));
        b.Add(consistency);
        b.AddRange(payload);
        return b.ToArray();
    }

    private static byte[] Emitter()
    {
        var a = new byte[32];
        for (int i = 0; i < 32; i++) a[i] = (byte)(i + 1);
        return a;
    }

    /// <summary>The canonical Wormhole signing digest = keccak256(keccak256(body)).</summary>
    private static byte[] Digest(byte[] body) =>
        Keccak256.ComputeHash(Keccak256.ComputeHash(body));

    private static string BuildVaaBase64(
        uint guardianSetIndex, IReadOnlyList<WireSig> sigs, byte[] body, byte version = 1)
    {
        var buf = new List<byte>();
        buf.Add(version);
        buf.AddRange(U32BE(guardianSetIndex));
        buf.Add((byte)sigs.Count);
        foreach (var sg in sigs)
        {
            buf.Add((byte)sg.Index);
            buf.AddRange(sg.R);
            buf.AddRange(sg.S);
            buf.Add(sg.V);
        }
        buf.AddRange(body);
        return Convert.ToBase64String(buf.ToArray());
    }

    private static Secp256k1VaaSignatureVerifier MakeVerifier(WormholeConfig cfg) =>
        new(Options.Create(cfg), NullLogger<Secp256k1VaaSignatureVerifier>.Instance);

    private static WormholeConfig CfgWith(int setIndex, params string[] addresses)
    {
        var cfg = new WormholeConfig
        {
            MinGuardianSignatures = 1,
            GuardianSets = new Dictionary<string, List<string>>
            {
                [setIndex.ToString()] = addresses.ToList()
            }
        };
        return cfg;
    }

    private static byte[] PrivKey(byte seed)
    {
        var k = new byte[32];
        for (int i = 0; i < 32; i++) k[i] = (byte)(seed + i);
        k[31] |= 1; // ensure non-zero
        return k;
    }

    private static string Addr0x(byte[] a) => "0x" + Convert.ToHexString(a).ToLowerInvariant();

    // ─────────────────────────── Verifier unit tests ───────────────────────────

    [Fact]
    public async Task CorrectSignature_CorrectGuardian_ReturnsTrue()
    {
        var (priv, addr) = Curve.FromPrivateKey(PrivKey(0x11));
        var body = BuildBody();
        var digest = Digest(body);
        var (r, s, v) = Curve.Sign(digest, priv);

        var cfg = CfgWith(0, Addr0x(addr));
        var verifier = MakeVerifier(cfg);

        var ok = await verifier.VerifySignatureAsync(
            digest, new WormholeVaaSignature { GuardianIndex = 0, R = r, S = s, V = v }, 0);

        ok.Should().BeTrue("a valid signature by the configured Guardian must verify");
    }

    [Fact]
    public async Task TamperedDigest_ReturnsFalse()
    {
        var (priv, addr) = Curve.FromPrivateKey(PrivKey(0x22));
        var digest = Digest(BuildBody());
        var (r, s, v) = Curve.Sign(digest, priv);

        var tampered = (byte[])digest.Clone();
        tampered[0] ^= 0xFF; // flip a bit

        var verifier = MakeVerifier(CfgWith(0, Addr0x(addr)));
        var ok = await verifier.VerifySignatureAsync(
            tampered, new WormholeVaaSignature { GuardianIndex = 0, R = r, S = s, V = v }, 0);

        ok.Should().BeFalse("a signature over a different digest must not verify");
    }

    [Fact]
    public async Task KeyNotInGuardianSet_ReturnsFalse()
    {
        var (signerPriv, _) = Curve.FromPrivateKey(PrivKey(0x33));
        var (_, otherAddr) = Curve.FromPrivateKey(PrivKey(0x99)); // different key
        var digest = Digest(BuildBody());
        var (r, s, v) = Curve.Sign(digest, signerPriv);

        // Guardian set contains a DIFFERENT address than the actual signer.
        var verifier = MakeVerifier(CfgWith(0, Addr0x(otherAddr)));
        var ok = await verifier.VerifySignatureAsync(
            digest, new WormholeVaaSignature { GuardianIndex = 0, R = r, S = s, V = v }, 0);

        ok.Should().BeFalse("a signer not in the Guardian set must be rejected");
    }

    [Fact]
    public async Task WrongRecoveryId_ReturnsFalse()
    {
        var (priv, addr) = Curve.FromPrivateKey(PrivKey(0x44));
        var digest = Digest(BuildBody());
        var (r, s, v) = Curve.Sign(digest, priv);

        byte wrongV = (byte)(v ^ 1); // flip 0<->1
        var verifier = MakeVerifier(CfgWith(0, Addr0x(addr)));
        var ok = await verifier.VerifySignatureAsync(
            digest, new WormholeVaaSignature { GuardianIndex = 0, R = r, S = s, V = wrongV }, 0);

        ok.Should().BeFalse(
            "the wrong recovery id recovers a different (or no) public key");
    }

    [Fact]
    public async Task MalformedR_Zero_ReturnsFalse()
    {
        var (_, addr) = Curve.FromPrivateKey(PrivKey(0x55));
        var digest = Digest(BuildBody());
        var verifier = MakeVerifier(CfgWith(0, Addr0x(addr)));

        var ok = await verifier.VerifySignatureAsync(
            digest,
            new WormholeVaaSignature { GuardianIndex = 0, R = new byte[32], S = NonZero32(), V = 0 },
            0);

        ok.Should().BeFalse("r == 0 is not a valid ECDSA component");
    }

    [Fact]
    public async Task MalformedS_GreaterOrEqualCurveOrder_ReturnsFalse()
    {
        var (_, addr) = Curve.FromPrivateKey(PrivKey(0x56));
        var digest = Digest(BuildBody());

        // s = n (the secp256k1 group order) — out of range, must be rejected.
        byte[] sEqualsN = To32(Curve.N);
        var verifier = MakeVerifier(CfgWith(0, Addr0x(addr)));
        var ok = await verifier.VerifySignatureAsync(
            digest,
            new WormholeVaaSignature { GuardianIndex = 0, R = NonZero32(), S = sEqualsN, V = 0 },
            0);

        ok.Should().BeFalse("s >= curve order n must be rejected");
    }

    [Fact]
    public async Task GuardianIndexOutOfRangeForConfiguredSet_ReturnsFalse()
    {
        var (priv, addr) = Curve.FromPrivateKey(PrivKey(0x57));
        var digest = Digest(BuildBody());
        var (r, s, v) = Curve.Sign(digest, priv);

        // Set has exactly 1 guardian (index 0). Claiming index 5 is out of range.
        var verifier = MakeVerifier(CfgWith(0, Addr0x(addr)));
        var ok = await verifier.VerifySignatureAsync(
            digest, new WormholeVaaSignature { GuardianIndex = 5, R = r, S = s, V = v }, 0);

        ok.Should().BeFalse("a guardian index past the configured set is rejected");
    }

    [Fact]
    public async Task UnknownGuardianSetIndex_ReturnsFalse()
    {
        var (priv, addr) = Curve.FromPrivateKey(PrivKey(0x58));
        var digest = Digest(BuildBody());
        var (r, s, v) = Curve.Sign(digest, priv);

        // Config only has set 0; the VAA claims set 7 (no config ⇒ fail-closed).
        var verifier = MakeVerifier(CfgWith(0, Addr0x(addr)));
        var ok = await verifier.VerifySignatureAsync(
            digest, new WormholeVaaSignature { GuardianIndex = 0, R = r, S = s, V = v }, 7);

        ok.Should().BeFalse("an unconfigured Guardian-set index must fail closed");
    }

    [Fact]
    public async Task EmptyConfiguredSet_Placeholder_FailsClosed()
    {
        // Mirrors the unfilled mainnet/testnet placeholder in appsettings.json.
        var (priv, addr) = Curve.FromPrivateKey(PrivKey(0x59));
        var digest = Digest(BuildBody());
        var (r, s, v) = Curve.Sign(digest, priv);

        var cfg = new WormholeConfig
        {
            GuardianSets = new Dictionary<string, List<string>> { ["4"] = new() }
        };
        var verifier = MakeVerifier(cfg);
        var ok = await verifier.VerifySignatureAsync(
            digest, new WormholeVaaSignature { GuardianIndex = 0, R = r, S = s, V = v }, 4);

        ok.Should().BeFalse(
            "an empty placeholder Guardian set can never verify any signature");
    }

    [Fact]
    public async Task RoundTrip_IsDeterministic_AcrossInvocations()
    {
        var (priv, addr) = Curve.FromPrivateKey(PrivKey(0x60));
        var digest = Digest(BuildBody(sequence: 12345, nonce: 88));
        var (r, s, v) = Curve.Sign(digest, priv);
        var verifier = MakeVerifier(CfgWith(0, Addr0x(addr)));
        var sig = new WormholeVaaSignature { GuardianIndex = 0, R = r, S = s, V = v };

        var a = await verifier.VerifySignatureAsync(digest, sig, 0);
        var b = await verifier.VerifySignatureAsync(digest, sig, 0);
        var c = await verifier.VerifySignatureAsync(digest, sig, 0);

        a.Should().BeTrue();
        b.Should().Be(a);
        c.Should().Be(a);
    }

    /// <summary>
    /// Independently proves the well-known Wormhole devnet ("tilt") Guardian
    /// address shipped in appsettings.Development.json is the public key of the
    /// documented deterministic devnet Guardian private key. This is the source
    /// of the devnet constant — derived, not guessed.
    /// </summary>
    [Fact]
    public void DevnetTiltGuardianAddress_IsDerivedFromTheKnownDevnetPrivateKey()
    {
        // Standard Wormhole devnet/tilt Guardian private key (Wormhole Tiltfile /
        // @certusone/wormhole-sdk devnet guardian-set constant).
        byte[] devnetPriv = Convert.FromHexString(
            "cfb12303a19cde580bb4dd771639b0d26bc68353645571a8cff516ab2ee113a0");

        var (_, addr) = Curve.FromPrivateKey(devnetPriv);

        Addr0x(addr).Should().Be(
            "0xbefa429d57cd18b7f8a4d91a2da9ab4af05d0fbe",
            "this is the canonical Wormhole devnet Guardian address in " +
            "appsettings.Development.json (GuardianSets:0)");
    }

    // ─────────────────── Adapter integration (real verifier) ───────────────────

    [Fact]
    public async Task Adapter_RealVerifier_ValidVaa_VerifiesSuccessfully()
    {
        // 1 guardian, MinGuardianSignatures=1 — adapter still owns quorum/order.
        var (priv, addr) = Curve.FromPrivateKey(PrivKey(0x70));
        var body = BuildBody(sequence: 777);
        var digest = Digest(body);
        var (r, s, v) = Curve.Sign(digest, priv);
        var vaaB64 = BuildVaaBase64(0, new[] { new WireSig(0, r, s, v) }, body);

        var cfg = CfgWith(0, Addr0x(addr));
        cfg.MinGuardianSignatures = 1;
        var adapter = MakeAdapter(cfg);
        var vaa = new WormholeVAA { VaaBytes = vaaB64, Version = 1, SignatureCount = 1 };

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().BeTrue();
        result.Message.Should().Contain("cryptographically verified");
    }

    [Fact]
    public async Task Adapter_RealVerifier_TamperedBody_Rejected()
    {
        var (priv, addr) = Curve.FromPrivateKey(PrivKey(0x71));
        var body = BuildBody(sequence: 100);
        var digest = Digest(body);
        var (r, s, v) = Curve.Sign(digest, priv);

        // Sign the original body but ship a DIFFERENT body in the VAA.
        var tamperedBody = BuildBody(sequence: 101);
        var vaaB64 = BuildVaaBase64(0, new[] { new WireSig(0, r, s, v) }, tamperedBody);

        var cfg = CfgWith(0, Addr0x(addr));
        cfg.MinGuardianSignatures = 1;
        var adapter = MakeAdapter(cfg);
        var vaa = new WormholeVAA { VaaBytes = vaaB64, Version = 1, SignatureCount = 1 };

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeTrue("the signature does not cover the shipped body");
        result.Result.Should().BeFalse();
        result.Message.Should().Contain("failed secp256k1 verification");
    }

    [Fact]
    public async Task Adapter_RealVerifier_SignerNotInGuardianSet_Rejected()
    {
        var (signerPriv, _) = Curve.FromPrivateKey(PrivKey(0x72));
        var (_, otherAddr) = Curve.FromPrivateKey(PrivKey(0xA0));
        var body = BuildBody();
        var digest = Digest(body);
        var (r, s, v) = Curve.Sign(digest, signerPriv);
        var vaaB64 = BuildVaaBase64(0, new[] { new WireSig(0, r, s, v) }, body);

        var cfg = CfgWith(0, Addr0x(otherAddr)); // signer NOT this address
        cfg.MinGuardianSignatures = 1;
        var adapter = MakeAdapter(cfg);
        var vaa = new WormholeVAA { VaaBytes = vaaB64, Version = 1, SignatureCount = 1 };

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeTrue();
        result.Result.Should().BeFalse();
    }

    [Fact]
    public async Task Adapter_RealVerifier_BelowQuorum_RejectedByAdapter_NotVerifier()
    {
        // Guardian set of 19 ⇒ Byzantine quorum = (19*2/3)+1 = 13. Provide ONE
        // genuinely-valid signature. The verifier would pass it, but the ADAPTER
        // owns quorum and must reject below 13. Proves the division of duty.
        var (priv, addr) = Curve.FromPrivateKey(PrivKey(0x73));
        var body = BuildBody();
        var digest = Digest(body);
        var (r, s, v) = Curve.Sign(digest, priv);
        var vaaB64 = BuildVaaBase64(0, new[] { new WireSig(0, r, s, v) }, body);

        var addresses = new List<string> { Addr0x(addr) };
        for (int i = 1; i < 19; i++) // pad set to size 19 (others never sign)
            addresses.Add("0x" + new string('0', 40));
        var cfg = new WormholeConfig
        {
            MinGuardianSignatures = 1,
            ExpectedGuardianSetSize = 19,
            GuardianSets = new Dictionary<string, List<string>> { ["0"] = addresses }
        };
        var adapter = MakeAdapter(cfg);
        var vaa = new WormholeVAA { VaaBytes = vaaB64, Version = 1, SignatureCount = 1 };

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeTrue();
        result.Result.Should().BeFalse();
        result.Message.Should().Contain("below Wormhole quorum",
            "quorum is the adapter's responsibility, not the per-signature verifier's");
    }

    [Fact]
    public async Task Adapter_RealVerifier_MultiGuardian_AllValid_Verifies()
    {
        // 3 distinct guardians, strictly-increasing indices 0,1,2 — exercises
        // the per-signature loop with the real verifier end-to-end.
        var g0 = Curve.FromPrivateKey(PrivKey(0x80));
        var g1 = Curve.FromPrivateKey(PrivKey(0x81));
        var g2 = Curve.FromPrivateKey(PrivKey(0x82));
        var body = BuildBody(sequence: 9001);
        var digest = Digest(body);

        var s0 = Curve.Sign(digest, g0.priv);
        var s1 = Curve.Sign(digest, g1.priv);
        var s2 = Curve.Sign(digest, g2.priv);
        var sigs = new[]
        {
            new WireSig(0, s0.r, s0.s, s0.v),
            new WireSig(1, s1.r, s1.s, s1.v),
            new WireSig(2, s2.r, s2.s, s2.v),
        };
        var vaaB64 = BuildVaaBase64(0, sigs, body);

        var cfg = new WormholeConfig
        {
            MinGuardianSignatures = 3,
            GuardianSets = new Dictionary<string, List<string>>
            {
                ["0"] = new() { Addr0x(g0.address), Addr0x(g1.address), Addr0x(g2.address) }
            }
        };
        var adapter = MakeAdapter(cfg);
        var vaa = new WormholeVAA { VaaBytes = vaaB64, Version = 1, SignatureCount = 3 };

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().BeTrue();
    }

    [Fact]
    public async Task Adapter_NoVerifier_DefaultConfig_StillFailsClosed()
    {
        // Regression guard: the adapter built WITHOUT a verifier under the
        // secure default must still fail closed (the existing Moq tests rely on
        // this; registering Secp256k1VaaSignatureVerifier must not change it).
        var (priv, addr) = Curve.FromPrivateKey(PrivKey(0x90));
        var body = BuildBody();
        var digest = Digest(body);
        var (r, s, v) = Curve.Sign(digest, priv);
        var vaaB64 = BuildVaaBase64(0, new[] { new WireSig(0, r, s, v) }, body);

        var cfg = CfgWith(0, Addr0x(addr));
        cfg.MinGuardianSignatures = 1;
        var adapter = MakeAdapter(cfg, withVerifier: false);
        var vaa = new WormholeVAA { VaaBytes = vaaB64, Version = 1, SignatureCount = 1 };

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeTrue();
        result.Result.Should().BeFalse();
        result.Message.Should().Contain(
            "secp256k1 signature verification not available — refusing to treat VAA as trusted");
    }

    // ─── helpers ───

    private static byte[] NonZero32()
    {
        var b = new byte[32];
        Array.Fill(b, (byte)0x07);
        return b;
    }

    private static byte[] To32(BcBigInteger v)
    {
        byte[] raw = v.ToByteArrayUnsigned();
        if (raw.Length == 32) return raw;
        var p = new byte[32];
        Array.Copy(raw, 0, p, 32 - raw.Length, Math.Min(raw.Length, 32));
        return p;
    }

    private static WormholeAdapter MakeAdapter(WormholeConfig cfg, bool withVerifier = true)
    {
        var factory = new Mock<IBlockchainProviderFactory>();
        var logger = new Mock<ILogger<WormholeAdapter>>();
        var http = new HttpClient(new StubHandler())
        {
            BaseAddress = new Uri("https://test-guardian.example.com")
        };
        IVaaSignatureVerifier? verifier = withVerifier ? MakeVerifier(cfg) : null;
        return new WormholeAdapter(
            http, factory.Object, Options.Create(cfg), logger.Object, verifier);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
    }
}
