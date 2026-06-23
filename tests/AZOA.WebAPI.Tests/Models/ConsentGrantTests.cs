using FluentAssertions;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Tests.Models;

/// <summary>
/// tenant-consent-delegation AC5/M4: the live-validity predicate the signing seam
/// evaluates on EVERY tenant-driven sign. Revocation and expiry take effect on the
/// next sign attempt — the TTL of an already-issued child JWT is NOT the cutoff.
/// </summary>
public class ConsentGrantTests
{
    private static ConsentGrant Grant(DateTime? expires = null, DateTime? revoked = null, params string[] scopes)
        => new()
        {
            GrantorAvatarId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Scopes = scopes.ToList(),
            GrantedAt = DateTime.UtcNow.AddMinutes(-5),
            ExpiresAt = expires,
            RevokedAt = revoked,
        };

    [Fact]
    public void IsLiveAt_True_WhenNotRevokedNotExpired()
        => Grant().IsLiveAt(DateTime.UtcNow).Should().BeTrue();

    [Fact]
    public void IsLiveAt_False_WhenRevoked()
        => Grant(revoked: DateTime.UtcNow.AddMinutes(-1)).IsLiveAt(DateTime.UtcNow).Should().BeFalse();

    [Fact]
    public void IsLiveAt_False_WhenExpired()
        => Grant(expires: DateTime.UtcNow.AddMinutes(-1)).IsLiveAt(DateTime.UtcNow).Should().BeFalse();

    [Fact]
    public void Covers_True_WhenLiveAndScopePresent()
        => Grant(scopes: AzoaScopes.SwapSign).Covers(AzoaScopes.SwapSign, DateTime.UtcNow).Should().BeTrue();

    [Fact]
    public void Covers_False_WhenScopeAbsent()
        => Grant(scopes: AzoaScopes.QuestExecute).Covers(AzoaScopes.SwapSign, DateTime.UtcNow).Should().BeFalse();

    [Fact]
    public void Covers_False_WhenRevoked_EvenIfScopePresent()
        => Grant(revoked: DateTime.UtcNow.AddMinutes(-1), scopes: AzoaScopes.SwapSign)
            .Covers(AzoaScopes.SwapSign, DateTime.UtcNow).Should().BeFalse();

    [Fact]
    public void Covers_False_ForBlankScope()
        => Grant(scopes: AzoaScopes.SwapSign).Covers("", DateTime.UtcNow).Should().BeFalse();

    [Fact]
    public void GrantActLiveRevoke_ImmediatelyDeniesNextCheck_AC5()
    {
        // grant → act (covered) → revoke → immediately denied (NOT after TTL).
        var g = Grant(scopes: AzoaScopes.SwapSign);
        var t0 = DateTime.UtcNow;
        g.Covers(AzoaScopes.SwapSign, t0).Should().BeTrue();          // act: allowed

        g.RevokedAt = t0.AddSeconds(1);                               // revoke
        g.Covers(AzoaScopes.SwapSign, t0.AddSeconds(2)).Should().BeFalse(); // next check: denied
    }
}
