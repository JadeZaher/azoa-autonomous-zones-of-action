using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using AZOA.WebAPI.Mcp;

namespace AZOA.WebAPI.Tests.Mcp;

/// <summary>
/// Unit tests for <see cref="McpAuthMiddleware"/>.
/// All cases hand-roll a <see cref="DefaultHttpContext"/> (no Moq needed —
/// DefaultHttpContext is a concrete, non-sealed ASP.NET type that covers the
/// full Items/Response/Path/User surface the middleware touches).
/// </summary>
[Trait("Category", "Mcp")]
public class McpAuthScopingTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    /// Builds a DefaultHttpContext on the /mcp path with the given ClaimsPrincipal.
    private static DefaultHttpContext McpContext(ClaimsPrincipal? user = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/mcp";
        if (user is not null)
            ctx.User = user;
        ctx.Response.Body = new System.IO.MemoryStream();
        return ctx;
    }

    /// Builds an unauthenticated DefaultHttpContext on a non-MCP path.
    private static DefaultHttpContext NonMcpContext(string path = "/api/holon")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Response.Body = new System.IO.MemoryStream();
        return ctx;
    }

    private static ClaimsPrincipal PrincipalWith(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "TestScheme"));

    private static ClaimsPrincipal EmptyPrincipal() =>
        new(new ClaimsIdentity()); // no claims, not authenticated

    /// Wires the middleware with a no-op next delegate and returns the delegate.
    private static (McpAuthMiddleware middleware, bool[] nextCalled) BuildMiddleware()
    {
        var nextCalled = new bool[1];
        RequestDelegate next = _ =>
        {
            nextCalled[0] = true;
            return Task.CompletedTask;
        };
        return (new McpAuthMiddleware(next), nextCalled);
    }

    // ── test cases ────────────────────────────────────────────────────────────

    [Fact]
    public async Task MissingAvatarClaim_Returns401()
    {
        var (mw, nextCalled) = BuildMiddleware();
        var ctx = McpContext(EmptyPrincipal());

        await mw.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(401);
        nextCalled[0].Should().BeFalse("next must not be called when auth fails");
        ctx.Items.ContainsKey("mcp.avatar_id").Should().BeFalse();
    }

    [Fact]
    public async Task MalformedAvatarClaim_Returns401()
    {
        var (mw, nextCalled) = BuildMiddleware();
        // Claim value is present but not a valid Guid.
        var user = PrincipalWith(new Claim(ClaimTypes.NameIdentifier, "not-a-guid"));
        var ctx = McpContext(user);

        await mw.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(401);
        nextCalled[0].Should().BeFalse();
        ctx.Items.ContainsKey("mcp.avatar_id").Should().BeFalse();
    }

    [Fact]
    public async Task ValidJwtAvatarClaim_StashesAvatarIdOnHttpContext()
    {
        // JWT scheme populates ClaimTypes.NameIdentifier (ASP.NET's default
        // mapping of the "sub" JWT claim). TestAuthHandler does the same.
        var (mw, nextCalled) = BuildMiddleware();
        var avatarId = Guid.NewGuid();
        var user = PrincipalWith(new Claim(ClaimTypes.NameIdentifier, avatarId.ToString()));
        var ctx = McpContext(user);

        await mw.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(200, "successful middleware should not change status");
        nextCalled[0].Should().BeTrue();
        ctx.Items["mcp.avatar_id"].Should().Be(avatarId);
    }

    [Fact]
    public async Task ValidApiKeyAvatarClaim_StashesAvatarIdOnHttpContext()
    {
        // ApiKeyAuthenticationHandler emits three copies of the AvatarId claim.
        // Here we simulate the "AvatarId" custom claim to verify the third
        // fallback branch in the middleware's resolution chain.
        var (mw, nextCalled) = BuildMiddleware();
        var avatarId = Guid.NewGuid();
        // Deliberately omit ClaimTypes.NameIdentifier and "sub" so the middleware
        // must fall through to the "AvatarId" custom claim.
        var user = PrincipalWith(new Claim("AvatarId", avatarId.ToString()));
        var ctx = McpContext(user);

        await mw.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(200);
        nextCalled[0].Should().BeTrue();
        ctx.Items["mcp.avatar_id"].Should().Be(avatarId);
    }

    [Fact]
    public async Task NonMcpPath_PassesThrough()
    {
        // A request to /api/holon (or any path outside /mcp) must not be
        // touched by this middleware even if the user has no avatar claim.
        var (mw, nextCalled) = BuildMiddleware();
        var ctx = NonMcpContext("/api/holon");
        // No user claims at all — would fail if the /mcp check were absent.
        ctx.User = EmptyPrincipal();

        await mw.InvokeAsync(ctx);

        // Default status code for a context where nothing set it is 200.
        ctx.Response.StatusCode.Should().Be(200);
        nextCalled[0].Should().BeTrue("non-MCP paths must pass through unconditionally");
        ctx.Items.ContainsKey("mcp.avatar_id").Should().BeFalse();
    }
}
