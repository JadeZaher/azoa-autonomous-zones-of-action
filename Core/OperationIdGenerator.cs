using System.Security.Cryptography;

namespace AZOA.WebAPI.Core;

/// <summary>
/// Generates <b>deterministic, content-addressed</b> operation IDs for
/// blockchain operations that require client-side signing. These are NOT
/// transaction hashes — they are stable references the client can use to look
/// up (and de-duplicate) an operation later.
///
/// Determinism guarantee: the same logical operation inputs
/// (<c>chain</c>, <c>operationType</c>, <c>walletAddress</c> [, <c>parameters…</c>])
/// always produce the SAME id — every call, every process, forever. There is
/// no time component. This makes the id usable as an idempotency key: identical
/// requests collapse to one operation.
///
/// <para><b>Separator-safe:</b> every component (chain, operationType,
/// walletAddress and each parameter) is individually escaped via
/// <see cref="Uri.EscapeDataString(string)"/> before being joined with
/// <c>|</c>, so distinct logical inputs can never alias onto the same digest
/// (e.g. <c>("a|b","c")</c> ≠ <c>("a","b|c")</c> ≠ <c>("a","b","c")</c>).</para>
///
/// <para><b>Collision-resistant:</b> the digest is the FULL SHA-256 (64 hex
/// chars / 256 bits), not a truncated prefix — appropriate for a lifetime
/// idempotency key where a collision silently suppresses a distinct
/// value-moving operation.</para>
///
/// Format: <c>op_{chain}_{operationType}_{sha256Hex}</c> (the hash segment is
/// the full 64-char lowercase SHA-256). The total key length stays well within
/// the 200-char <c>IdempotencyRecord.Key</c> bound.
/// </summary>
public static class OperationIdGenerator
{
    /// <summary>
    /// Generate a deterministic operation ID from operation metadata.
    /// Same (chain, operationType, walletAddress) ⇒ same id, always.
    /// </summary>
    public static string Generate(string chain, string operationType, string walletAddress)
    {
        var input = string.Join("|",
            Escape(chain.ToLowerInvariant()),
            Escape(operationType.ToLowerInvariant()),
            Escape(walletAddress));
        return Format(chain, operationType, input);
    }

    /// <summary>
    /// Generate a deterministic operation ID including additional parameter
    /// data so that operations differing only by parameters get distinct ids.
    /// Same (chain, operationType, walletAddress, parameters…) ⇒ same id, always.
    /// </summary>
    public static string Generate(string chain, string operationType, string walletAddress, params object[] parameters)
    {
        // Escape EVERY component (incl. each param) before joining so that no
        // component value can introduce a spurious separator and alias onto a
        // different logical operation's key.
        var parts = new List<string>(3 + parameters.Length)
        {
            Escape(chain.ToLowerInvariant()),
            Escape(operationType.ToLowerInvariant()),
            Escape(walletAddress)
        };
        parts.AddRange(parameters.Select(p => Escape(p?.ToString() ?? "")));
        var input = string.Join("|", parts);
        return Format(chain, operationType, input);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string Format(string chain, string operationType, string input)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        // Full 256-bit digest — a truncated prefix is collision-thin for a
        // lifetime idempotency key (a collision silently drops a distinct op).
        var hashHex = AZOA.WebAPI.Helpers.Encoding.ToLowerHex(hash);

        return $"op_{chain.ToLowerInvariant()}_{operationType.ToLowerInvariant()}_{hashHex}";
    }
}
