using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services;

namespace AZOA.WebAPI.Tests.Services;

/// <summary>
/// Security tests for <see cref="WormholeAdapter"/> VAA verification. Every
/// test asserts the fail-closed post-hardening security property.
/// </summary>
public class WormholeAdapterTests
{
    private readonly Mock<IBlockchainProviderFactory> _factoryMock;
    private readonly Mock<IBlockchainProvider> _providerMock;
    private readonly WormholeConfig _config;
    private readonly Mock<ILogger<WormholeAdapter>> _loggerMock;

    public WormholeAdapterTests()
    {
        _factoryMock = new Mock<IBlockchainProviderFactory>();
        _providerMock = new Mock<IBlockchainProvider>();
        _loggerMock = new Mock<ILogger<WormholeAdapter>>();
        _config = new WormholeConfig
        {
            GuardianRpcUrl = "https://test-guardian.example.com",
            VaaTimeoutSeconds = 5,
            VaaPollIntervalMs = 100,
            MinGuardianSignatures = 13,
            ChainMappings = new Dictionary<string, WormholeChainMapping>
            {
                ["Solana"] = new() { WormholeChainId = 1, CoreBridgeAddress = "worm2ZoG..." },
                ["Algorand"] = new() { WormholeChainId = 8, CoreBridgeAddress = "842125965" }
            }
        };
    }

    private WormholeAdapter CreateAdapter(
        HttpClient? httpClient = null,
        IVaaSignatureVerifier? signatureVerifier = null)
    {
        var client = httpClient ?? new HttpClient(new FakeHandler(HttpStatusCode.OK, "{}"))
        {
            BaseAddress = new Uri(_config.GuardianRpcUrl)
        };
        return new WormholeAdapter(
            client,
            _factoryMock.Object,
            Options.Create(_config),
            _loggerMock.Object,
            signatureVerifier);
    }

    // VAA wire-format builder. MUST mirror WormholeAdapter.ParseVAA exactly —
    // any drift here silently invalidates every verification test:
    //   Header: [0] version | [1..4] guardianSetIndex u32 BE | [5] sigCount L |
    //           [6 .. 6+66L) L sigs of 66B: [+0] idx, [+1..32] r, [+33..64] s, [+65] v
    //   Body (from sigSectionEnd = 6+66L): ts u32 | nonce u32 | emitterChain u16 |
    //           emitter 32B | sequence u64 BE | consistency 1B | payload rest
    // ParseVAA sets StructurallyParsed=true only when len >= 6 AND
    // sigSectionEnd + 51 (bodyPrefix 4+4+2+32+8+1) <= len. Returns base64.

    private sealed record GuardianSig(int Index, byte[] R, byte[] S, byte V);

    private static GuardianSig MakeSig(int index, byte fill = 0xAB)
    {
        var r = new byte[32];
        var s = new byte[32];
        Array.Fill(r, fill);
        Array.Fill(s, (byte)(fill ^ 0xFF));
        return new GuardianSig(index, r, s, 1);
    }

    /// <summary>
    /// Build N well-formed signatures with strictly-increasing, unique guardian
    /// indices 0..N-1 (the canonical Wormhole ordering).
    /// </summary>
    private static List<GuardianSig> MakeSigs(int count)
    {
        var list = new List<GuardianSig>(count);
        for (int i = 0; i < count; i++)
            list.Add(MakeSig(i, (byte)(0x10 + i)));
        return list;
    }

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

    /// <summary>
    /// Construct a Wormhole VAA wire byte array and return it base64-encoded
    /// (the form WormholeVAA.VaaBytes / ParseVAA / ComputeVaaDigest consume).
    /// </summary>
    private static string BuildVaaBase64(
        byte version = 1,
        uint guardianSetIndex = 0,
        IEnumerable<GuardianSig>? signatures = null,
        uint timestamp = 1_700_000_000,
        uint nonce = 7,
        int emitterChain = 1,
        byte[]? emitterAddress = null,
        ulong sequence = 42,
        byte consistencyLevel = 1,
        byte[]? payload = null)
    {
        var sigs = (signatures ?? MakeSigs(13)).ToList();
        emitterAddress ??= BuildEmitterAddress();
        payload ??= new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        if (emitterAddress.Length != 32)
            throw new ArgumentException("emitterAddress must be exactly 32 bytes");

        var buf = new List<byte>();

        // ─── Header ───
        buf.Add(version);                       // [0]
        buf.AddRange(U32BE(guardianSetIndex));  // [1..4]
        buf.Add((byte)sigs.Count);              // [5] sigCount

        foreach (var sg in sigs)
        {
            if (sg.R.Length != 32 || sg.S.Length != 32)
                throw new ArgumentException("r/s must be 32 bytes");
            buf.Add((byte)sg.Index);            // +0  guardian index
            buf.AddRange(sg.R);                 // +1..+32
            buf.AddRange(sg.S);                 // +33..+64
            buf.Add(sg.V);                      // +65 recovery id
        }

        // ─── Body (signed region) ───
        buf.AddRange(U32BE(timestamp));         // timestamp
        buf.AddRange(U32BE(nonce));             // nonce
        buf.AddRange(U16BE(emitterChain));      // emitterChain
        buf.AddRange(emitterAddress);           // 32-byte emitter
        buf.AddRange(U64BE(sequence));          // sequence
        buf.Add(consistencyLevel);              // consistency
        buf.AddRange(payload);                  // payload

        return Convert.ToBase64String(buf.ToArray());
    }

    /// <summary>A non-zero 32-byte emitter address (all-zero is rejected by W2).</summary>
    private static byte[] BuildEmitterAddress()
    {
        var a = new byte[32];
        for (int i = 0; i < 32; i++) a[i] = (byte)(i + 1);
        return a;
    }

    /// <summary>Build a WormholeVAA whose fields agree with its wire bytes.</summary>
    private static WormholeVAA WrapVaa(
        string vaaBase64,
        int version = 1,
        int signatureCount = 13,
        int emitterChainId = 1,
        long sequence = 42)
    {
        return new WormholeVAA
        {
            VaaBytes = vaaBase64,
            Version = version,
            SignatureCount = signatureCount,
            EmitterChainId = emitterChainId,
            EmitterAddress = Convert.ToHexString(BuildEmitterAddress()).ToLowerInvariant(),
            Sequence = sequence
        };
    }

    // ─── Unchanged metadata-only tests (no security regression) ───

    [Fact]
    public void IsRouteSupported_SolanaToAlgorand_ReturnsTrue()
    {
        var adapter = CreateAdapter();
        adapter.IsRouteSupported("Solana", "Algorand").Should().BeTrue();
    }

    [Fact]
    public void IsRouteSupported_SameChain_ReturnsFalse()
    {
        var adapter = CreateAdapter();
        adapter.IsRouteSupported("Solana", "Solana").Should().BeFalse();
    }

    [Fact]
    public void IsRouteSupported_UnknownChain_ReturnsFalse()
    {
        var adapter = CreateAdapter();
        adapter.IsRouteSupported("Ethereum", "Solana").Should().BeFalse();
    }

    [Fact]
    public void GetWormholeChainId_Solana_Returns1()
    {
        var adapter = CreateAdapter();
        adapter.GetWormholeChainId("Solana").Should().Be(1);
    }

    [Fact]
    public void GetWormholeChainId_Algorand_Returns8()
    {
        var adapter = CreateAdapter();
        adapter.GetWormholeChainId("Algorand").Should().Be(8);
    }

    [Fact]
    public void GetWormholeChainId_Unknown_ReturnsNull()
    {
        var adapter = CreateAdapter();
        adapter.GetWormholeChainId("Bitcoin").Should().BeNull();
    }

    [Fact]
    public async Task InitiateTransferAsync_UnsupportedRoute_ReturnsError()
    {
        var adapter = CreateAdapter();

        var result = await adapter.InitiateTransferAsync(
            "Ethereum", "Solana", "token1", "sender", "recipient", 1);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not supported");
    }

    [Fact]
    public async Task InitiateTransferAsync_LockFails_ReturnsError()
    {
        _providerMock.Setup(p => p.LockForBridgeAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<string> { IsError = true, Message = "Lock failed" });

        _factoryMock.Setup(f => f.GetProvider("Solana", ChainNetwork.Devnet))
            .Returns(_providerMock.Object);

        var adapter = CreateAdapter();
        var result = await adapter.InitiateTransferAsync(
            "Solana", "Algorand", "token1", "sender", "recipient", 1);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("lock failed");
    }

    [Fact]
    public async Task InitiateTransferAsync_Success_ReturnsInitiation()
    {
        _providerMock.Setup(p => p.LockForBridgeAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<string> { IsError = false, Result = "tx_hash_123" });

        _factoryMock.Setup(f => f.GetProvider("Solana", ChainNetwork.Devnet))
            .Returns(_providerMock.Object);

        var adapter = CreateAdapter();
        var result = await adapter.InitiateTransferAsync(
            "Solana", "Algorand", "token1", "sender", "recipient", 5);

        result.IsError.Should().BeFalse();
        result.Result.Should().NotBeNull();
        result.Result!.TxHash.Should().Be("tx_hash_123");
        result.Result.EmitterChainId.Should().Be(1); // Solana
    }

    [Fact]
    public async Task VerifyVAAAsync_EmptyBytes_ReturnsError()
    {
        var adapter = CreateAdapter();
        var vaa = new WormholeVAA { VaaBytes = "" };

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("empty");
    }

    // Malformed/short VAA ⇒ verification fails closed. The attacker-controlled
    // SignatureCount field cannot manufacture trust without parsed bytes.

    [Fact]
    public async Task VerifyVAAAsync_JunkBytes_AQID_FailsClosed_CannotReturnOkTrue()
    {
        // base64("AQID") = bytes 0x01 0x02 0x03, caller claims 13 sigs.
        var adapter = CreateAdapter();
        var vaa = new WormholeVAA { VaaBytes = "AQID", SignatureCount = 13, Version = 1 };

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeTrue(
            "3 junk bytes cannot be a valid VAA regardless of caller SignatureCount");
        result.Result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyVAAAsync_TruncatedHeader_StructurallyInvalid_ReturnsError()
    {
        // 5 bytes — one short of the 6-byte minimum header. ParseVAA leaves
        // StructurallyParsed=false; W2 rejects with the truncation message.
        var truncated = Convert.ToBase64String(new byte[] { 1, 0, 0, 0, 0 });
        var adapter = CreateAdapter();
        var vaa = new WormholeVAA { VaaBytes = truncated, Version = 1, SignatureCount = 0 };

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("structurally invalid");
        result.Result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyVAAAsync_HeaderOkButBodyTruncated_ReturnsError()
    {
        // Valid 6-byte header (version=1, gsi=0, sigCount=0) but ZERO body
        // bytes — the 51-byte fixed body prefix does not fit. ParseVAA leaves
        // StructurallyParsed=false; must reject.
        var headerOnly = Convert.ToBase64String(new byte[] { 1, 0, 0, 0, 0, 0 });
        var adapter = CreateAdapter();
        var vaa = new WormholeVAA { VaaBytes = headerOnly, Version = 1, SignatureCount = 0 };

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("structurally invalid");
        result.Result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyVAAAsync_UnsupportedVersion_ReturnsError()
    {
        var vaaBytes = BuildVaaBase64(version: 2, signatures: MakeSigs(13));
        var adapter = CreateAdapter();
        var vaa = WrapVaa(vaaBytes, version: 2);

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("version");
        result.Result.Should().BeFalse();
    }

    // Structural validity ≠ cryptographic trust: even a perfectly valid VAA
    // fails closed with no verifier when RequireFullSignatureVerification (the
    // default) is true.

    [Fact]
    public async Task VerifyVAAAsync_StructurallyValid_NoVerifier_DefaultConfig_FailsClosed()
    {
        // RequireFullSignatureVerification defaults to true. No quorum set, so
        // 13 sigs >= MinGuardianSignatures(13) and ALL structural checks pass.
        _config.RequireFullSignatureVerification.Should().BeTrue(
            "this is the secure default the test depends on");

        var vaaBytes = BuildVaaBase64(signatures: MakeSigs(13));
        var adapter = CreateAdapter(signatureVerifier: null); // NO verifier
        var vaa = WrapVaa(vaaBytes);

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeTrue(
            "structurally valid VAA must still fail closed without a real verifier");
        result.Message.Should().Contain(
            "secp256k1 signature verification not available — refusing to treat VAA as trusted");
        result.Result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyVAAAsync_StructurallyValid_NoVerifier_RequireFalse_StructuralOnlyPass()
    {
        // Explicit opt-out: structural-only acceptance is allowed ONLY when
        // RequireFullSignatureVerification is explicitly false. Proves the
        // escape hatch exists but is OFF by default (covered above).
        _config.RequireFullSignatureVerification = false;

        var vaaBytes = BuildVaaBase64(signatures: MakeSigs(13));
        var adapter = CreateAdapter(signatureVerifier: null);
        var vaa = WrapVaa(vaaBytes);

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
        result.Message.Should().Contain("NOT cryptographically verified");
    }

    // Wormhole requires signatures ordered by strictly-increasing unique
    // guardian index (duplicate = replay padding, descending = forgery signal).
    // RequireFullSignatureVerification is false so the fail-closed gate does
    // not mask the ordering guard under test.

    [Fact]
    public async Task VerifyVAAAsync_DuplicateGuardianIndex_Rejected()
    {
        _config.RequireFullSignatureVerification = false; // isolate the ordering guard

        // 13 sigs but index 5 appears twice (positions 5 and 6 both index 5).
        var sigs = MakeSigs(13);
        sigs[6] = MakeSig(5, 0x99); // duplicate of sigs[5].Index == 5

        var vaaBytes = BuildVaaBase64(signatures: sigs);
        var adapter = CreateAdapter();
        var vaa = WrapVaa(vaaBytes);

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not strictly increasing");
        result.Result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyVAAAsync_DescendingGuardianIndices_Rejected()
    {
        _config.RequireFullSignatureVerification = false;

        // 13 well-formed sigs but in DESCENDING index order (12..0).
        var sigs = new List<GuardianSig>();
        for (int i = 12; i >= 0; i--) sigs.Add(MakeSig(i, (byte)(0x20 + i)));

        var vaaBytes = BuildVaaBase64(signatures: sigs);
        var adapter = CreateAdapter();
        var vaa = WrapVaa(vaaBytes);

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not strictly increasing");
        result.Result.Should().BeFalse();
    }

    // Byzantine quorum floor(2/3·N)+1 enforced when ExpectedGuardianSetSize is
    // configured; the count is derived from parsed bytes, not caller input.

    [Fact]
    public async Task VerifyVAAAsync_BelowQuorum_Rejected()
    {
        // Guardian set size 19 ⇒ quorum = (19*2/3)+1 = 13. Provide only 12
        // well-formed, correctly-ordered sigs. Lower MinGuardianSignatures so
        // the quorum check (not the min-sig check) is what rejects.
        _config.ExpectedGuardianSetSize = 19;
        _config.MinGuardianSignatures = 1;
        _config.RequireFullSignatureVerification = false;

        var vaaBytes = BuildVaaBase64(signatures: MakeSigs(12));
        var adapter = CreateAdapter();
        var vaa = WrapVaa(vaaBytes, signatureCount: 12);

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("below Wormhole quorum");
        result.Result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyVAAAsync_AtQuorum_WithPassingVerifier_Proceeds()
    {
        // Set size 19 ⇒ quorum 13. Provide exactly 13 sigs + a verifier that
        // approves every signature ⇒ the VAA is accepted.
        _config.ExpectedGuardianSetSize = 19;
        _config.MinGuardianSignatures = 13;

        var verifier = new Mock<IVaaSignatureVerifier>();
        verifier.Setup(v => v.VerifySignatureAsync(
                It.IsAny<byte[]>(), It.IsAny<WormholeVaaSignature>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var vaaBytes = BuildVaaBase64(signatures: MakeSigs(13));
        var adapter = CreateAdapter(signatureVerifier: verifier.Object);
        var vaa = WrapVaa(vaaBytes);

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
        verifier.Verify(v => v.VerifySignatureAsync(
            It.IsAny<byte[]>(), It.IsAny<WormholeVaaSignature>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(13));
    }

    [Fact]
    public async Task VerifyVAAAsync_GuardianIndexOutOfRange_Rejected()
    {
        // Set size 13 ⇒ valid indices 0..12. Make the last signer index 99,
        // which is out of range for the configured set (forgery signal).
        _config.ExpectedGuardianSetSize = 13;
        _config.MinGuardianSignatures = 1;
        _config.RequireFullSignatureVerification = false;

        var sigs = MakeSigs(13);
        sigs[12] = MakeSig(99, 0x77); // still strictly-increasing, but out of range

        var vaaBytes = BuildVaaBase64(signatures: sigs);
        var adapter = CreateAdapter();
        var vaa = WrapVaa(vaaBytes);

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("out of range");
        result.Result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyVAAAsync_FewerThanMinGuardianSignatures_Rejected()
    {
        // No quorum configured; only MinGuardianSignatures (13) applies.
        // 12 well-formed ordered sigs ⇒ rejected by the min-sig gate.
        _config.ExpectedGuardianSetSize = null;
        _config.MinGuardianSignatures = 13;
        _config.RequireFullSignatureVerification = false;

        var vaaBytes = BuildVaaBase64(signatures: MakeSigs(12));
        var adapter = CreateAdapter();
        var vaa = WrapVaa(vaaBytes, signatureCount: 12);

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Insufficient Guardian signatures");
        result.Result.Should().BeFalse();
    }

    // Verified path: a verifier approving all ⇒ Ok(true); rejecting any single
    // signature ⇒ the whole VAA is rejected.

    [Fact]
    public async Task VerifyVAAAsync_FakeVerifierApprovesAll_ReturnsOkTrue()
    {
        _config.MinGuardianSignatures = 13;

        var verifier = new Mock<IVaaSignatureVerifier>();
        verifier.Setup(v => v.VerifySignatureAsync(
                It.IsAny<byte[]>(), It.IsAny<WormholeVaaSignature>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var vaaBytes = BuildVaaBase64(signatures: MakeSigs(13));
        var adapter = CreateAdapter(signatureVerifier: verifier.Object);
        var vaa = WrapVaa(vaaBytes);

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
        result.Message.Should().Contain("cryptographically verified");
    }

    [Fact]
    public async Task VerifyVAAAsync_FakeVerifierRejectsOneSignature_WholeVaaRejected()
    {
        _config.MinGuardianSignatures = 13;

        // Approve everything EXCEPT guardian index 7 — a single bad Guardian
        // signature must poison the entire VAA.
        var verifier = new Mock<IVaaSignatureVerifier>();
        verifier.Setup(v => v.VerifySignatureAsync(
                It.IsAny<byte[]>(), It.IsAny<WormholeVaaSignature>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[] _, WormholeVaaSignature s, int _, CancellationToken _) =>
                s.GuardianIndex != 7);

        var vaaBytes = BuildVaaBase64(signatures: MakeSigs(13));
        var adapter = CreateAdapter(signatureVerifier: verifier.Object);
        var vaa = WrapVaa(vaaBytes);

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("failed secp256k1 verification");
        result.Result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyVAAAsync_VerifierReceivesCanonicalKeccakDigest_NotRehashed()
    {
        // The verifier must receive the 32-byte canonical signing digest
        // (keccak256(keccak256(body))), independently recomputed here, and the
        // adapter must NOT re-hash it. Guards the digest contract with W2.
        _config.MinGuardianSignatures = 13;

        byte[]? captured = null;
        var verifier = new Mock<IVaaSignatureVerifier>();
        verifier.Setup(v => v.VerifySignatureAsync(
                It.IsAny<byte[]>(), It.IsAny<WormholeVaaSignature>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((byte[] d, WormholeVaaSignature _, int _, CancellationToken _) => captured ??= d)
            .ReturnsAsync(true);

        var emitter = BuildEmitterAddress();
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var vaaBytes = BuildVaaBase64(
            signatures: MakeSigs(13),
            timestamp: 1_700_000_123,
            nonce: 99,
            emitterChain: 1,
            emitterAddress: emitter,
            sequence: 4242,
            consistencyLevel: 1,
            payload: payload);

        var adapter = CreateAdapter(signatureVerifier: verifier.Object);
        var vaa = WrapVaa(vaaBytes, sequence: 4242);

        var result = await adapter.VerifyVAAAsync(vaa);

        result.IsError.Should().BeFalse();
        captured.Should().NotBeNull();
        captured!.Length.Should().Be(32, "canonical VAA digest is keccak256 → 32 bytes");

        // Independently recompute keccak256(keccak256(body)). Body = signed
        // region: ts(4)+nonce(4)+emitterChain(2)+emitter(32)+seq(8)+consistency(1)+payload.
        var body = new List<byte>();
        body.AddRange(U32BE(1_700_000_123));
        body.AddRange(U32BE(99));
        body.AddRange(U16BE(1));
        body.AddRange(emitter);
        body.AddRange(U64BE(4242));
        body.Add(1);
        body.AddRange(payload);
        var expectedDigest = Keccak256.ComputeHash(Keccak256.ComputeHash(body.ToArray()));

        captured.Should().Equal(expectedDigest,
            "the adapter must hand the verifier the exact keccak256(keccak256(body)) bytes");
    }

    // ComputeVaaDigest is the exact ConsumedVaas replay-ledger key: must be
    // lowercase-hex SHA256(base64Decode(VaaBytes)), deterministic, and identical
    // across both overloads. Recomputed independently to catch cross-component drift.

    [Fact]
    public void ComputeVaaDigest_EqualsLowercaseHexSha256OfBase64DecodedBytes()
    {
        var vaaBytes = BuildVaaBase64(signatures: MakeSigs(13));

        var actual = WormholeAdapter.ComputeVaaDigest(vaaBytes);

        var raw = Convert.FromBase64String(vaaBytes);
        var expected = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();

        actual.Should().Be(expected);
        actual.Should().MatchRegex("^[0-9a-f]{64}$", "SHA256 hex is 64 lowercase hex chars");
    }

    [Fact]
    public void ComputeVaaDigest_IsDeterministic_AcrossInvocations()
    {
        var vaaBytes = BuildVaaBase64(signatures: MakeSigs(13));

        var d1 = WormholeAdapter.ComputeVaaDigest(vaaBytes);
        var d2 = WormholeAdapter.ComputeVaaDigest(vaaBytes);

        d1.Should().Be(d2, "the replay-ledger key must be stable for the same VAA");
    }

    [Fact]
    public void ComputeVaaDigest_StringAndVaaOverloads_Agree()
    {
        var vaaBytes = BuildVaaBase64(signatures: MakeSigs(13));
        var vaa = WrapVaa(vaaBytes);

        var fromString = WormholeAdapter.ComputeVaaDigest(vaaBytes);
        var fromVaa = WormholeAdapter.ComputeVaaDigest(vaa);

        fromVaa.Should().Be(fromString,
            "both ConsumedVaas digest entry points must compute the same key");
    }

    [Fact]
    public void ComputeVaaDigest_DistinctVaas_ProduceDistinctDigests()
    {
        var a = BuildVaaBase64(signatures: MakeSigs(13), sequence: 1);
        var b = BuildVaaBase64(signatures: MakeSigs(13), sequence: 2);

        WormholeAdapter.ComputeVaaDigest(a)
            .Should().NotBe(WormholeAdapter.ComputeVaaDigest(b),
                "different VAA bodies must not collide in the replay ledger");
    }

    [Fact]
    public void ComputeVaaDigest_EmptyBytes_Throws()
    {
        var act = () => WormholeAdapter.ComputeVaaDigest("");
        act.Should().Throw<ArgumentException>();
    }

    // RedeemTransferAsync / FetchVAAAsync: a junk/unverifiable VAA never mints;
    // only a genuinely verified VAA drives the target-chain mint.

    [Fact]
    public async Task RedeemTransferAsync_UnknownTarget_ReturnsError()
    {
        var adapter = CreateAdapter();
        var vaa = WrapVaa(BuildVaaBase64(signatures: MakeSigs(13)));

        var result = await adapter.RedeemTransferAsync("Ethereum", vaa, "recipient");

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Unknown target");
    }

    [Fact]
    public async Task RedeemTransferAsync_JunkVaa_FailsClosed_NeverMints()
    {
        // Junk "AQID" must fail VAA verification; MintWrapped must never be
        // called (no on-chain redemption from an unverifiable VAA).
        _factoryMock.Setup(f => f.GetProvider("Algorand", ChainNetwork.Devnet))
            .Returns(_providerMock.Object);

        var adapter = CreateAdapter();
        var vaa = new WormholeVAA { VaaBytes = "AQID", SignatureCount = 13, Version = 1 };

        var result = await adapter.RedeemTransferAsync("Algorand", vaa, "recipient");

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("VAA verification failed");
        _providerMock.Verify(p => p.MintWrappedAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "an unverifiable VAA must never trigger an on-chain mint");
    }

    [Fact]
    public async Task RedeemTransferAsync_NoVerifier_DefaultConfig_FailsClosed_NeverMints()
    {
        // Even a structurally-valid VAA must not redeem without a real verifier
        // under the secure default — proves redeem inherits the fail-closed gate.
        _factoryMock.Setup(f => f.GetProvider("Algorand", ChainNetwork.Devnet))
            .Returns(_providerMock.Object);

        var adapter = CreateAdapter(signatureVerifier: null);
        var vaa = WrapVaa(BuildVaaBase64(signatures: MakeSigs(13)));

        var result = await adapter.RedeemTransferAsync("Algorand", vaa, "recipient");

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("VAA verification failed");
        _providerMock.Verify(p => p.MintWrappedAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RedeemTransferAsync_VerifiedVaa_MintsOnTargetChain()
    {
        // GENUINE happy path: structurally-valid VAA + a fake verifier that
        // approves every signature ⇒ VAA verifies ⇒ target-chain mint runs.
        _config.MinGuardianSignatures = 13;

        var verifier = new Mock<IVaaSignatureVerifier>();
        verifier.Setup(v => v.VerifySignatureAsync(
                It.IsAny<byte[]>(), It.IsAny<WormholeVaaSignature>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _providerMock.Setup(p => p.MintWrappedAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<string> { IsError = false, Result = "mint_tx_456" });

        _factoryMock.Setup(f => f.GetProvider("Algorand", ChainNetwork.Devnet))
            .Returns(_providerMock.Object);

        var adapter = CreateAdapter(signatureVerifier: verifier.Object);
        var vaa = WrapVaa(BuildVaaBase64(signatures: MakeSigs(13), sequence: 42), sequence: 42);

        var result = await adapter.RedeemTransferAsync("Algorand", vaa, "recipient");

        result.IsError.Should().BeFalse();
        result.Result!.Success.Should().BeTrue();
        result.Result.TxHash.Should().Be("mint_tx_456");
    }

    [Fact]
    public async Task FetchVAAAsync_GuardianReturnsStructurallyValidVaa_ParsesFromBytes()
    {
        // The parser derives emitterChain/sequence/sigCount from the actual
        // bytes, not from caller-supplied fields.
        var emitter = BuildEmitterAddress();
        var vaaBase64 = BuildVaaBase64(
            signatures: MakeSigs(13),
            emitterChain: 1,
            emitterAddress: emitter,
            sequence: 42);

        var responseJson = JsonSerializer.Serialize(new { vaaBytes = vaaBase64 });
        var handler = new FakeHandler(HttpStatusCode.OK, responseJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(_config.GuardianRpcUrl) };

        var adapter = CreateAdapter(httpClient);
        var result = await adapter.FetchVAAAsync(1, "emitter_addr", 42);

        result.IsError.Should().BeFalse();
        result.Result!.StructurallyParsed.Should().BeTrue(
            "a properly-constructed VAA must parse");
        result.Result.Sequence.Should().Be(42);          // derived from body bytes
        result.Result.EmitterChainId.Should().Be(1);     // derived from body bytes
        result.Result.SignatureCount.Should().Be(13);    // derived from header byte[5]
        result.Result.Signatures.Should().HaveCount(13,   // actually parsed sig blocks
            "13 real 66-byte signature records must be present, not just a count byte");
    }

    [Fact]
    public async Task FetchVAAAsync_GuardianReturnsZeroBuffer_StructurallyInvalid_RejectedByVerify()
    {
        // 100-byte zero buffer with byte[0]=1, byte[5]=13. ParseVAA:
        // sigSectionEnd = 6 + 66*13 = 864 > 100 ⇒ StructurallyParsed=false.
        // Fetch parses leniently; Verify is the security boundary that rejects.
        var zeroBuf = new byte[100];
        zeroBuf[0] = 1;
        zeroBuf[5] = 13;
        var vaaBase64 = Convert.ToBase64String(zeroBuf);

        var responseJson = JsonSerializer.Serialize(new { vaaBytes = vaaBase64 });
        var handler = new FakeHandler(HttpStatusCode.OK, responseJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(_config.GuardianRpcUrl) };

        var adapter = CreateAdapter(httpClient);
        var fetched = await adapter.FetchVAAAsync(1, "emitter_addr", 42);

        // Whatever fetch surfaces, verification of that buffer MUST fail closed.
        fetched.IsError.Should().BeFalse("fetch parses leniently; the gate is Verify");
        fetched.Result!.StructurallyParsed.Should().BeFalse(
            "6 + 66*13 = 864 > 100 bytes ⇒ truncated");

        var verify = await adapter.VerifyVAAAsync(fetched.Result);
        verify.IsError.Should().BeTrue();
        verify.Message.Should().Contain("structurally invalid");
    }

    [Fact]
    public async Task FetchVAAAsync_Timeout_ReturnsError()
    {
        _config.VaaTimeoutSeconds = 1;
        _config.VaaPollIntervalMs = 200;

        var handler = new FakeHandler(HttpStatusCode.NotFound, "");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(_config.GuardianRpcUrl) };

        var adapter = CreateAdapter(httpClient);
        var result = await adapter.FetchVAAAsync(1, "emitter", 99);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("timed out");
    }

    // ─── Test HTTP handler ───

    private class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;

        public FakeHandler(HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
