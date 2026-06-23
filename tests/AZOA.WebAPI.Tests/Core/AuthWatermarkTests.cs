using System;
using FluentAssertions;
using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Tests.Core;

/// <summary>
/// user-sovereign-identity AC3b (security-review fix): the per-avatar AuthNotBefore
/// watermark cut. When a user CLAIMS an avatar, any credential minted BEFORE that
/// instant — notably a tenant child JWT issued in the window before the claim — is
/// stale and must be rejected on EVERY request. These cases pin the adversarial
/// boundary: a one-second grace for second-precision JWT timestamps, but anything
/// clearly before the claim is cut.
/// </summary>
public class AuthWatermarkTests
{
    private static readonly DateTime Base = new(2026, 6, 22, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0)]        // token at the base instant
    [InlineData(-600)]     // token 10 minutes in the past
    [InlineData(-86400)]   // token a day in the past
    [InlineData(600)]      // token in the future
    public void NullWatermark_NeverStale_ForAnyTokenInstant(int offsetSeconds)
    {
        // ATTACK BLOCKED: an avatar that was NEVER claimed has no watermark, so a
        // legitimately-minted token (at any instant) must not be spuriously rejected.
        var tokenInstant = Base.AddSeconds(offsetSeconds);
        AuthWatermark.IsTokenStale(tokenInstant, watermark: null).Should().BeFalse();
    }

    [Fact]
    public void TokenIssuedBeforeWatermark_IsStale()
    {
        // THE ATTACK: a tenant child JWT minted 10 minutes BEFORE the user claimed
        // their avatar must be rejected on every request post-claim — it cannot be
        // used to act on the now-self-sovereign avatar.
        var watermark = Base;
        var tokenInstant = Base.AddMinutes(-10);
        AuthWatermark.IsTokenStale(tokenInstant, watermark).Should().BeTrue();
    }

    [Fact]
    public void TokenIssuedAfterWatermark_IsNotStale()
    {
        // A credential minted AFTER the claim is legitimately the user's own token.
        var watermark = Base;
        var tokenInstant = Base.AddMinutes(10);
        AuthWatermark.IsTokenStale(tokenInstant, watermark).Should().BeFalse();
    }

    [Fact]
    public void TokenExactlyAtWatermark_IsNotStale_OneSecondGrace()
    {
        // A token minted in the same wall-clock second as the claim is honored — the
        // 1s grace absorbs second-precision JWT iat/nbf truncation.
        var watermark = Base;
        AuthWatermark.IsTokenStale(Base, watermark).Should().BeFalse();
    }

    [Fact]
    public void TokenOneSecondBeforeWatermark_IsNotStale_GraceBoundary()
    {
        // Exactly at the grace boundary (watermark - 1s): still honored, NOT stale.
        var watermark = Base;
        AuthWatermark.IsTokenStale(Base.AddSeconds(-1), watermark).Should().BeFalse();
    }

    [Fact]
    public void TokenTwoSecondsBeforeWatermark_IsStale_JustPastGrace()
    {
        // ATTACK BLOCKED: one second past the grace — a token clearly minted before
        // the claim is cut. The grace cannot be abused to slip a pre-claim token.
        var watermark = Base;
        AuthWatermark.IsTokenStale(Base.AddSeconds(-2), watermark).Should().BeTrue();
    }
}
