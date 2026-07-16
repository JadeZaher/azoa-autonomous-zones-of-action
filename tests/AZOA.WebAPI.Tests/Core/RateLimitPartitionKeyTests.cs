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
