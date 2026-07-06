using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Services;

namespace AZOA.WebAPI.Tests.Services;

/// <summary>
/// MEDIUM-2 (final-hardening-cutover G2): the emitter address written into the
/// consumed_vaa_ledger MUST match the schema ASSERT `^[0-9a-f]{64}$` so a legit
/// redeem is never false-rejected over casing/prefix. `ParseVAA` is the seam that
/// emits the parsed emitter; these pin that its output is always canonical
/// lowercase 64-hex regardless of the emitter byte pattern.
/// </summary>
public class WormholeEmitterCanonicalizationTests
{
    private static readonly Regex CanonicalEmitter =
        new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    // ─── ParseVAA (private static) invoked via reflection ───

    private static WormholeVAA InvokeParseVaa(string vaaBase64, int emitterChainId, string emitterAddress, long sequence)
    {
        var method = typeof(WormholeAdapter).GetMethod(
            "ParseVAA", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("ParseVAA is the canonical VAA parse seam under test");

        var result = method!.Invoke(null, new object[] { vaaBase64, emitterChainId, emitterAddress, sequence });
        return (WormholeVAA)result!;
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04 })]          // low bytes
    [InlineData(new byte[] { 0xAB, 0xCD, 0xEF, 0xFF })]          // high (would be upper-hex if not lowercased)
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x01 })]          // mostly-zero, non-all-zero
    public void ParseVaa_emitter_is_always_canonical_lower_64_hex(byte[] emitterSeed)
    {
        var emitter = new byte[32];
        Array.Copy(emitterSeed, 0, emitter, 32 - emitterSeed.Length, emitterSeed.Length);

        var vaaBase64 = BuildVaaBase64(emitterAddress: emitter);

        // Caller hints are deliberately mixed-case / prefixed to prove the parsed
        // body value (not the hint) is what lands, and that it is canonical.
        var vaa = InvokeParseVaa(vaaBase64, emitterChainId: 1, emitterAddress: "0xABCDEF", sequence: 42);

        vaa.StructurallyParsed.Should().BeTrue();
        vaa.EmitterAddress.Should().MatchRegex(CanonicalEmitter,
            "the consumed_vaa_ledger ASSERT is ^[0-9a-f]{64}$ and a non-canonical emitter false-rejects a valid redeem");
        vaa.Digest.Should().MatchRegex(CanonicalEmitter, "the digest is also lowercase 64-hex");
    }

    // ─── Minimal Wormhole VAA wire-format builder (mirrors ParseVAA layout) ───

    private static byte[] U32BE(uint v) =>
        new[] { (byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF) };

    private static byte[] U16BE(int v) =>
        new[] { (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF) };

    private static byte[] U64BE(ulong v)
    {
        var b = new byte[8];
        for (int i = 7; i >= 0; i--) { b[i] = (byte)(v & 0xFF); v >>= 8; }
        return b;
    }

    private static string BuildVaaBase64(byte[] emitterAddress)
    {
        const int sigCount = 13;
        var buf = new List<byte>();

        // Header: version(1) + guardianSetIndex(4) + sigCount(1).
        buf.Add(1);
        buf.AddRange(U32BE(0));
        buf.Add(sigCount);

        // Signature block: sigCount * 66 bytes (index + r + s + v). Contents are
        // irrelevant to emitter canonicalization — ParseVAA only needs them to
        // size the body offset.
        for (int i = 0; i < sigCount; i++)
        {
            buf.Add((byte)i);              // guardian index
            buf.AddRange(new byte[32]);    // r
            buf.AddRange(new byte[32]);    // s
            buf.Add(0);                    // v
        }

        // Body: timestamp(4) nonce(4) emitterChain(2) emitter(32) sequence(8) consistency(1) payload.
        buf.AddRange(U32BE(1_700_000_000));
        buf.AddRange(U32BE(7));
        buf.AddRange(U16BE(1));
        buf.AddRange(emitterAddress);
        buf.AddRange(U64BE(42));
        buf.Add(1);
        buf.AddRange(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        return Convert.ToBase64String(buf.ToArray());
    }
}
