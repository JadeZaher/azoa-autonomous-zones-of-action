// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Core;

/// <summary>
/// The per-avatar <c>AuthNotBefore</c> watermark check (user-sovereign-identity AC3b,
/// security-review fix). When a user CLAIMS their avatar, the avatar's
/// <c>AuthNotBefore</c> is stamped to "now"; any credential MINTED before that instant
/// (notably a tenant child JWT issued in the ≤15-min window before the claim) is stale
/// and must be rejected on EVERY request — not merely at mint time. The JwtBearer
/// <c>OnTokenValidated</c> event calls <see cref="IsTokenStale"/> with the token's
/// issued-at/not-before instant and the avatar's current watermark.
///
/// <para>Pure + static so the adversarial boundary cases (token exactly at the
/// watermark, one second before, far before, no watermark) are unit-testable without a
/// running JwtBearer pipeline.</para>
/// </summary>
public static class AuthWatermark
{
    /// <summary>
    /// True when a token issued at <paramref name="tokenInstant"/> is STALE relative to
    /// the avatar's <paramref name="watermark"/> and must be rejected. A null watermark
    /// (avatar never claimed) is never stale. A one-second grace is allowed for
    /// whole-second JWT timestamp truncation, so a token minted in the same second as
    /// the claim is honored, but anything issued clearly before the claim is rejected.
    /// </summary>
    public static bool IsTokenStale(DateTime tokenInstant, DateTime? watermark)
    {
        if (watermark is null)
            return false;

        // JWT iat/nbf are second-precision; allow a 1s grace so a token minted in the
        // same wall-clock second as the claim watermark is not spuriously rejected.
        return tokenInstant < watermark.Value.AddSeconds(-1);
    }
}
