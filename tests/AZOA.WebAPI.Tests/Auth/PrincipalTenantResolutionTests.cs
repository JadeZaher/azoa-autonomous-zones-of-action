using System.Security.Claims;
using AZOA.WebAPI.Helpers;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Auth;

/// <summary>GetActingTenantOrPrincipalTenantId precedence (Helpers/AGENTS.md §principal-tenant).</summary>
public sealed class PrincipalTenantResolutionTests
{
    private static readonly Guid Subject = Guid.NewGuid();
    private static readonly Guid ActingTenant = Guid.NewGuid();

    [Theory]
    [InlineData(true, true, "acting")]    // an explicit act_as_tenant claim wins even over the subject
    [InlineData(false, true, "acting")]
    [InlineData(true, false, "subject")]  // a tenant's own API key: the key owner IS the tenant
    [InlineData(false, false, "none")]    // a first-party human session registers the node's own unit
    public void ResolvesTheTenantFromThePrincipal(bool apiKey, bool actAsTenant, string expected)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Subject.ToString()), new("UserId", Subject.ToString()) };
        if (apiKey) claims.Add(new("AuthMethod", "ApiKey"));
        if (actAsTenant) claims.Add(new("act_as_tenant", ActingTenant.ToString()));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        principal.GetActingTenantOrPrincipalTenantId().Should().Be(expected switch
        {
            "acting" => ActingTenant,
            "subject" => Subject,
            _ => (Guid?)null,
        });
        principal.GetActingTenantId().Should().Be(actAsTenant ? ActingTenant : null, "the delegation axis is unchanged");
    }
}
