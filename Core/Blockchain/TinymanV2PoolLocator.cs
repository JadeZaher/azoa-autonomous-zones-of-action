using System.Text;

namespace AZOA.WebAPI.Core.Blockchain;

/// <summary>
/// Deterministic Tinyman V2 pool discovery.
///
/// A Tinyman V2 pool is a stateless logicsig account whose program is a fixed
/// template with the validator app id and the two (sorted) asset ids spliced
/// in at fixed offsets. The pool account's address is therefore fully derived
/// off-chain — no indexer scan or pair→pool registry is involved (the previous
/// implementation incorrectly assumed the validator app held such a registry).
///
/// Reference: tinyman-py-sdk `tinyman/v2/contracts.py::get_pool_logicsig` and
/// `tinyman/v2/constants.py::POOL_LOGICSIG_TEMPLATE`.
/// </summary>
public static class TinymanV2PoolLocator
{
    // Base64 of the Tinyman V2 pool logicsig template (47 bytes). The 8-byte
    // big-endian fields are spliced at: [3..11) validator app id,
    // [11..19) asset_1 id (= max of the pair), [19..27) asset_2 id (= min).
    private const string PoolLogicSigTemplate =
        "BoAYAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgQBbNQA0ADEYEkQxGYEBEkSBAUM=";

    /// <summary>Tinyman V2 validator (factory) app id per Algorand network.</summary>
    public const ulong TestnetValidatorAppId = 148607000UL;
    public const ulong MainnetValidatorAppId = 1002541853UL;

    /// <summary>
    /// Derive the 58-char Algorand address of the Tinyman V2 pool for the given
    /// asset pair. Asset order is irrelevant — the pair is sorted internally,
    /// matching Tinyman's own derivation.
    /// </summary>
    public static string GetPoolAddress(ulong validatorAppId, ulong assetAId, ulong assetBId)
    {
        var asset1Id = Math.Max(assetAId, assetBId); // Tinyman: asset_1 = max
        var asset2Id = Math.Min(assetAId, assetBId); // Tinyman: asset_2 = min

        var program = Convert.FromBase64String(PoolLogicSigTemplate);
        WriteUInt64BigEndian(program, 3, validatorAppId);
        WriteUInt64BigEndian(program, 11, asset1Id);
        WriteUInt64BigEndian(program, 19, asset2Id);

        // Algorand logicsig account address = Sha512_256("Program" || program).
        var prefixed = new byte[7 + program.Length];
        System.Text.Encoding.ASCII.GetBytes("Program").CopyTo(prefixed, 0);
        program.CopyTo(prefixed, 7);

        var publicKey = Sha512_256(prefixed); // 32 bytes
        return EncodeAlgorandAddress(publicKey);
    }

    /// <summary>Algorand address: base32(pubkey ‖ checksum) where checksum = Sha512_256(pubkey)[28..32].</summary>
    public static string EncodeAlgorandAddress(byte[] publicKey)
    {
        var checksum = Sha512_256(publicKey);
        var raw = new byte[36];
        publicKey.CopyTo(raw, 0);
        Array.Copy(checksum, 28, raw, 32, 4);
        return Base32NoPad(raw);
    }

    private static void WriteUInt64BigEndian(byte[] buffer, int offset, ulong value)
    {
        for (int i = 0; i < 8; i++)
            buffer[offset + i] = (byte)(value >> (56 - 8 * i));
    }

    // ─── RFC 4648 base32, no padding (Algorand alphabet) ───

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static string Base32NoPad(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                sb.Append(Base32Alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }
        if (bitsLeft > 0)
            sb.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        return sb.ToString();
    }

    // ─── SHA-512/256 (FIPS 180-4) — not provided by the .NET BCL ───

    private static readonly ulong[] K =
    {
        0x428a2f98d728ae22, 0x7137449123ef65cd, 0xb5c0fbcfec4d3b2f, 0xe9b5dba58189dbbc,
        0x3956c25bf348b538, 0x59f111f1b605d019, 0x923f82a4af194f9b, 0xab1c5ed5da6d8118,
        0xd807aa98a3030242, 0x12835b0145706fbe, 0x243185be4ee4b28c, 0x550c7dc3d5ffb4e2,
        0x72be5d74f27b896f, 0x80deb1fe3b1696b1, 0x9bdc06a725c71235, 0xc19bf174cf692694,
        0xe49b69c19ef14ad2, 0xefbe4786384f25e3, 0x0fc19dc68b8cd5b5, 0x240ca1cc77ac9c65,
        0x2de92c6f592b0275, 0x4a7484aa6ea6e483, 0x5cb0a9dcbd41fbd4, 0x76f988da831153b5,
        0x983e5152ee66dfab, 0xa831c66d2db43210, 0xb00327c898fb213f, 0xbf597fc7beef0ee4,
        0xc6e00bf33da88fc2, 0xd5a79147930aa725, 0x06ca6351e003826f, 0x142929670a0e6e70,
        0x27b70a8546d22ffc, 0x2e1b21385c26c926, 0x4d2c6dfc5ac42aed, 0x53380d139d95b3df,
        0x650a73548baf63de, 0x766a0abb3c77b2a8, 0x81c2c92e47edaee6, 0x92722c851482353b,
        0xa2bfe8a14cf10364, 0xa81a664bbc423001, 0xc24b8b70d0f89791, 0xc76c51a30654be30,
        0xd192e819d6ef5218, 0xd69906245565a910, 0xf40e35855771202a, 0x106aa07032bbd1b8,
        0x19a4c116b8d2d0c8, 0x1e376c085141ab53, 0x2748774cdf8eeb99, 0x34b0bcb5e19b48a8,
        0x391c0cb3c5c95a63, 0x4ed8aa4ae3418acb, 0x5b9cca4f7763e373, 0x682e6ff3d6b2b8a3,
        0x748f82ee5defb2fc, 0x78a5636f43172f60, 0x84c87814a1f0ab72, 0x8cc702081a6439ec,
        0x90befffa23631e28, 0xa4506cebde82bde9, 0xbef9a3f7b2c67915, 0xc67178f2e372532b,
        0xca273eceea26619c, 0xd186b8c721c0c207, 0xeada7dd6cde0eb1e, 0xf57d4f7fee6ed178,
        0x06f067aa72176fba, 0x0a637dc5a2c898a6, 0x113f9804bef90dae, 0x1b710b35131c471b,
        0x28db77f523047d84, 0x32caab7b40c72493, 0x3c9ebe0a15c9bebc, 0x431d67c49c100d4c,
        0x4cc5d4becb3e42b6, 0x597f299cfc657e2a, 0x5fcb6fab3ad6faec, 0x6c44198c4a475817,
    };

    /// <summary>Computes the 32-byte SHA-512/256 digest of <paramref name="message"/>.</summary>
    public static byte[] Sha512_256(byte[] message)
    {
        // SHA-512/256 initial hash values (FIPS 180-4 §5.3.6.2).
        ulong h0 = 0x22312194FC2BF72C, h1 = 0x9F555FA3C84C64C2,
              h2 = 0x2393B86B6F53B151, h3 = 0x963877195940EABD,
              h4 = 0x96283EE2A88EFFE3, h5 = 0xBE5E1E2553863992,
              h6 = 0x2B0199FC2C85B8AA, h7 = 0x0EB72DDC81C52CA2;

        // Padding: 0x80, then zeros, then 128-bit big-endian bit length.
        long bitLen = (long)message.Length * 8;
        int padLen = (int)(((112 - (message.Length + 1) % 128) + 128) % 128);
        var padded = new byte[message.Length + 1 + padLen + 16];
        message.CopyTo(padded, 0);
        padded[message.Length] = 0x80;
        for (int i = 0; i < 8; i++)
            padded[padded.Length - 1 - i] = (byte)(bitLen >> (8 * i));

        var w = new ulong[80];
        for (int block = 0; block < padded.Length; block += 128)
        {
            for (int t = 0; t < 16; t++)
            {
                int o = block + t * 8;
                w[t] = ((ulong)padded[o] << 56) | ((ulong)padded[o + 1] << 48) |
                       ((ulong)padded[o + 2] << 40) | ((ulong)padded[o + 3] << 32) |
                       ((ulong)padded[o + 4] << 24) | ((ulong)padded[o + 5] << 16) |
                       ((ulong)padded[o + 6] << 8) | padded[o + 7];
            }
            for (int t = 16; t < 80; t++)
            {
                ulong s0 = Ror(w[t - 15], 1) ^ Ror(w[t - 15], 8) ^ (w[t - 15] >> 7);
                ulong s1 = Ror(w[t - 2], 19) ^ Ror(w[t - 2], 61) ^ (w[t - 2] >> 6);
                w[t] = w[t - 16] + s0 + w[t - 7] + s1;
            }

            ulong a = h0, b = h1, c = h2, d = h3, e = h4, f = h5, g = h6, h = h7;
            for (int t = 0; t < 80; t++)
            {
                ulong S1 = Ror(e, 14) ^ Ror(e, 18) ^ Ror(e, 41);
                ulong ch = (e & f) ^ (~e & g);
                ulong t1 = h + S1 + ch + K[t] + w[t];
                ulong S0 = Ror(a, 28) ^ Ror(a, 34) ^ Ror(a, 39);
                ulong maj = (a & b) ^ (a & c) ^ (b & c);
                ulong t2 = S0 + maj;
                h = g; g = f; f = e; e = d + t1;
                d = c; c = b; b = a; a = t1 + t2;
            }
            h0 += a; h1 += b; h2 += c; h3 += d;
            h4 += e; h5 += f; h6 += g; h7 += h;
        }

        // SHA-512/256 output = leftmost 256 bits = H0..H3.
        var digest = new byte[32];
        WriteUInt64BigEndian(digest, 0, h0);
        WriteUInt64BigEndian(digest, 8, h1);
        WriteUInt64BigEndian(digest, 16, h2);
        WriteUInt64BigEndian(digest, 24, h3);
        return digest;
    }

    private static ulong Ror(ulong x, int n) => (x >> n) | (x << (64 - n));
}
