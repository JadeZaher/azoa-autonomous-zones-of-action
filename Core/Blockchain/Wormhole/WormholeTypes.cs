using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Core.Blockchain.Wormhole;

/// <summary>
/// A Verified Action Approval — the cryptographic proof produced by the
/// Wormhole Guardian network attesting that an event occurred on a source chain.
/// </summary>
public class WormholeVAA
{
    /// <summary>VAA version (currently 1).</summary>
    public int Version { get; set; } = 1;

    /// <summary>Index of the Guardian set that signed this VAA.</summary>
    public int GuardianSetIndex { get; set; }

    /// <summary>Number of Guardian signatures on this VAA.</summary>
    public int SignatureCount { get; set; }

    /// <summary>Raw VAA bytes encoded as base64.</summary>
    public string VaaBytes { get; set; } = string.Empty;

    /// <summary>Wormhole chain ID of the source chain.</summary>
    public int EmitterChainId { get; set; }

    /// <summary>Address of the emitting contract on the source chain (hex-encoded, 32 bytes).</summary>
    public string EmitterAddress { get; set; } = string.Empty;

    /// <summary>Monotonically increasing sequence number from the emitter.</summary>
    public long Sequence { get; set; }

    /// <summary>
    /// Canonical Wormhole VAA digest = keccak256(keccak256(body)), lowercase hex.
    /// This is the value Guardian signatures are computed over.
    /// </summary>
    public string? Digest { get; set; }

    /// <summary>When the VAA was observed by the Guardians.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Application-level payload inside the VAA.</summary>
    public string? Payload { get; set; }

    // ─── Parsed structural fields (populated by ParseVAA; additive) ───

    /// <summary>VAA body nonce.</summary>
    public long Nonce { get; set; }

    /// <summary>Consistency level (finality) byte from the VAA body.</summary>
    public int ConsistencyLevel { get; set; }

    /// <summary>
    /// Parsed Guardian signatures (guardian-set index + secp256k1 r/s/v).
    /// Empty when the VAA has not been structurally parsed.
    /// </summary>
    public List<WormholeVaaSignature> Signatures { get; set; } = new();

    /// <summary>
    /// True only when ParseVAA fully decoded the header + body without
    /// running out of bytes. A false value here MUST be treated as an
    /// unverifiable (rejected) VAA.
    /// </summary>
    public bool StructurallyParsed { get; set; }
}

/// <summary>
/// A single Guardian signature on a VAA: the signer's index into the
/// Guardian set, plus the secp256k1 (r, s, v) signature components.
/// </summary>
public sealed class WormholeVaaSignature
{
    /// <summary>Index of the signing Guardian within its Guardian set.</summary>
    public int GuardianIndex { get; set; }

    /// <summary>secp256k1 signature R component (32 bytes).</summary>
    public byte[] R { get; set; } = System.Array.Empty<byte>();

    /// <summary>secp256k1 signature S component (32 bytes).</summary>
    public byte[] S { get; set; } = System.Array.Empty<byte>();

    /// <summary>secp256k1 recovery id (0 or 1).</summary>
    public byte V { get; set; }
}

/// <summary>
/// Extension seam for real cryptographic VAA signature verification.
///
/// The project intentionally carries minimal external dependencies and does
/// NOT bundle a secp256k1 / ecrecover implementation. Wire a concrete
/// implementation of this interface (e.g. backed by a vetted secp256k1
/// library) into DI to enable full per-signature Guardian verification.
///
/// When this seam is NOT registered and
/// <see cref="WormholeConfig.RequireFullSignatureVerification"/> is true,
/// <c>WormholeAdapter.VerifyVAAAsync</c> FAILS CLOSED — it refuses to treat
/// any VAA as trusted rather than silently skipping the cryptographic check.
/// </summary>
public interface IVaaSignatureVerifier
{
    /// <summary>
    /// Recover the signer address for a single Guardian signature over the
    /// canonical VAA digest and confirm it is the expected Guardian for
    /// <paramref name="signature"/>.GuardianIndex within
    /// <paramref name="guardianSetIndex"/>.
    /// </summary>
    /// <param name="digest">
    /// The 32-byte canonical VAA digest = keccak256(keccak256(body)) — the
    /// exact bytes Guardians sign (NOT re-hashed by the verifier).
    /// </param>
    /// <returns>True iff the signature is cryptographically valid and maps to
    /// the expected Guardian; false otherwise.</returns>
    Task<bool> VerifySignatureAsync(
        byte[] digest,
        WormholeVaaSignature signature,
        int guardianSetIndex,
        CancellationToken ct = default);
}

/// <summary>
/// Pure managed Keccak-256 (the pre-NIST padding used by Ethereum and
/// Wormhole). NOTE: .NET's <c>SHA3_256</c> is NIST SHA3 which uses a
/// DIFFERENT domain-separation/padding byte and is NOT interchangeable with
/// Keccak-256 — hence this minimal self-contained implementation. No external
/// dependency is added. This computes a hash only; it does NOT and cannot
/// perform secp256k1 ecrecover.
/// </summary>
public static class Keccak256
{
    private const int Rounds = 24;

    private static readonly ulong[] RoundConstants =
    {
        0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808aUL,
        0x8000000080008000UL, 0x000000000000808bUL, 0x0000000080000001UL,
        0x8000000080008081UL, 0x8000000000008009UL, 0x000000000000008aUL,
        0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000aUL,
        0x000000008000808bUL, 0x800000000000008bUL, 0x8000000000008089UL,
        0x8000000000008003UL, 0x8000000000008002UL, 0x8000000000000080UL,
        0x000000000000800aUL, 0x800000008000000aUL, 0x8000000080008081UL,
        0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL
    };

    private static readonly int[] RotationOffsets =
    {
        0, 1, 62, 28, 27, 36, 44, 6, 55, 20, 3, 10, 43, 25,
        39, 41, 45, 15, 21, 8, 18, 2, 61, 56, 14
    };

    /// <summary>Compute the 32-byte Keccak-256 hash of <paramref name="input"/>.</summary>
    public static byte[] ComputeHash(byte[] input)
    {
        const int rate = 136; // 1088-bit rate for 256-bit output (capacity 512)
        var state = new ulong[25];

        int offset = 0;
        int fullBlocks = input.Length / rate;
        for (int b = 0; b < fullBlocks; b++)
        {
            Absorb(state, input, offset, rate);
            KeccakF(state);
            offset += rate;
        }

        // Final block with Keccak (0x01) padding + 0x80 final bit.
        int remaining = input.Length - offset;
        var block = new byte[rate];
        System.Array.Copy(input, offset, block, 0, remaining);
        block[remaining] = 0x01;
        block[rate - 1] |= 0x80;
        Absorb(state, block, 0, rate);
        KeccakF(state);

        var output = new byte[32];
        for (int i = 0; i < 4; i++)
            System.BitConverter.GetBytes(state[i]).CopyTo(output, i * 8);
        return output;
    }

    private static void Absorb(ulong[] state, byte[] data, int offset, int rate)
    {
        int laneCount = rate / 8;
        for (int i = 0; i < laneCount; i++)
            state[i] ^= System.BitConverter.ToUInt64(data, offset + i * 8);
    }

    private static void KeccakF(ulong[] a)
    {
        var c = new ulong[5];
        var d = new ulong[5];
        var b = new ulong[25];

        for (int round = 0; round < Rounds; round++)
        {
            // θ
            for (int x = 0; x < 5; x++)
                c[x] = a[x] ^ a[x + 5] ^ a[x + 10] ^ a[x + 15] ^ a[x + 20];
            for (int x = 0; x < 5; x++)
                d[x] = c[(x + 4) % 5] ^ Rol(c[(x + 1) % 5], 1);
            for (int x = 0; x < 5; x++)
                for (int y = 0; y < 5; y++)
                    a[x + 5 * y] ^= d[x];

            // ρ and π
            for (int x = 0; x < 5; x++)
                for (int y = 0; y < 5; y++)
                {
                    int idx = x + 5 * y;
                    b[y + 5 * ((2 * x + 3 * y) % 5)] = Rol(a[idx], RotationOffsets[idx]);
                }

            // χ
            for (int x = 0; x < 5; x++)
                for (int y = 0; y < 5; y++)
                    a[x + 5 * y] = b[x + 5 * y] ^ (~b[(x + 1) % 5 + 5 * y] & b[(x + 2) % 5 + 5 * y]);

            // ι
            a[0] ^= RoundConstants[round];
        }
    }

    private static ulong Rol(ulong value, int offset)
        => offset == 0 ? value : (value << offset) | (value >> (64 - offset));
}

/// <summary>
/// Result of a Wormhole transfer initiation (source-chain side).
/// Contains the data needed to fetch the VAA from the Guardian network.
/// </summary>
public class WormholeTransferInitiation
{
    public string TxHash { get; set; } = string.Empty;
    public int EmitterChainId { get; set; }
    public string EmitterAddress { get; set; } = string.Empty;
    public long Sequence { get; set; }
}

/// <summary>
/// Result of redeeming (completing) a Wormhole transfer on the target chain.
/// </summary>
public class WormholeRedemptionResult
{
    public string TxHash { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

// ─── Guardian API response DTOs ───

/// <summary>
/// Response from api.wormholescan.io/v1/signed_vaa/{chain}/{emitter}/{seq}.
/// WormholeScan returns vaaBytes at the top level (no data envelope).
/// </summary>
public class GuardianVAAEnvelope
{
    [JsonPropertyName("vaaBytes")]
    public string? VaaBytes { get; set; }

    [JsonPropertyName("vaa")]
    public string? Vaa { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
}
