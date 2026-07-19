using System.Net;
using System.Security.Claims;
using AZOA.WebAPI.Core.Networking;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AZOA.WebAPI.Tests.Core;

public sealed class RateLimitPartitionKeyTests
{
    [Fact]
    public void AnonymousInvalidApiKeyHeaders_CannotRotateTheIpPartition()
    {
        var first = AnonymousContext("invalid-one");
        var second = AnonymousContext("invalid-two");

        RateLimitPartitionKey.Resolve(first).Should().Be("ip:203.0.113.7");
        RateLimitPartitionKey.Resolve(second).Should().Be("ip:203.0.113.7");
    }

    [Fact]
    public void AuthenticatedApiKey_UsesServerIssuedKeyId()
    {
        var context = new DefaultHttpContext
        {
            User = Principal(
                new Claim("AuthMethod", "ApiKey"),
                new Claim("ApiKeyId", "6a4575a6-4187-46ad-a248-baa0b9823138"),
                new Claim(ClaimTypes.NameIdentifier, "avatar-id")),
        };
        context.Request.Headers["X-Api-Key"] = "raw-secret-is-not-the-partition";

        RateLimitPartitionKey.Resolve(context).Should()
            .Be("apikey:6a4575a6-4187-46ad-a248-baa0b9823138");
    }

    [Fact]
    public void TenantCustodialPartition_IsStablePerTenantAndExternalSubject()
    {
        var tenantId = Guid.NewGuid().ToString("D");
        var first = new DefaultHttpContext { User = Principal(new Claim(ClaimTypes.NameIdentifier, tenantId)) };
        var same = new DefaultHttpContext { User = Principal(new Claim(ClaimTypes.NameIdentifier, tenantId)) };
        var other = new DefaultHttpContext { User = Principal(new Claim(ClaimTypes.NameIdentifier, tenantId)) };
        first.Request.RouteValues["externalSubject"] = "arda-user-42";
        same.Request.RouteValues["externalSubject"] = " arda-user-42 ";
        other.Request.RouteValues["externalSubject"] = "arda-user-43";

        var firstKey = RateLimitPartitionKey.ResolveTenantSubject(first);

        firstKey.Should().Be(RateLimitPartitionKey.ResolveTenantSubject(same));
        firstKey.Should().NotBe(RateLimitPartitionKey.ResolveTenantSubject(other));
        firstKey.Should().StartWith($"tenant-subject:{tenantId}:");
        firstKey.Should().NotContain("arda-user-42");
    }

    [Fact]
    public void TenantApiKeys_ShareTenantAggregatePartitionAcrossKeyRotation()
    {
        var tenantId = Guid.NewGuid().ToString("D");
        var first = new DefaultHttpContext
        {
            User = Principal(
                new Claim("AuthMethod", "ApiKey"),
                new Claim("ApiKeyId", Guid.NewGuid().ToString("D")),
                new Claim(ClaimTypes.NameIdentifier, tenantId),
                new Claim("scope", AZOA.WebAPI.Core.AzoaScopes.TenantProvision))
        };
        var rotated = new DefaultHttpContext
        {
            User = Principal(
                new Claim("AuthMethod", "ApiKey"),
                new Claim("ApiKeyId", Guid.NewGuid().ToString("D")),
                new Claim(ClaimTypes.NameIdentifier, tenantId),
                new Claim("scope", AZOA.WebAPI.Core.AzoaScopes.TenantProvision))
        };

        RateLimitPartitionKey.Resolve(first).Should().Be($"tenant:{tenantId}");
        RateLimitPartitionKey.Resolve(rotated).Should().Be($"tenant:{tenantId}");
    }

    private static DefaultHttpContext AnonymousContext(string apiKey)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        context.Request.Headers["X-Api-Key"] = new StringValues(apiKey);
        return context;
    }

    private static ClaimsPrincipal Principal(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "authenticated"));
}
