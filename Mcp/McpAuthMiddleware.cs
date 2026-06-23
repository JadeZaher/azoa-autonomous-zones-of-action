namespace AZOA.WebAPI.Mcp;

using System.Security.Claims;

/// <summary>
/// Extracts the AvatarId claim from the authenticated HttpContext and stashes
/// it in <c>ctx.Items["mcp.avatar_id"]</c> so the MCP tool dispatcher can
/// build a <see cref="ToolCallContext"/> with the correct identity.
///
/// Enforced only on <c>/mcp</c> paths. Rejects (401) if no parseable AvatarId
/// claim is present — every MCP tool runs inside an avatar scope; there is no
/// anonymous MCP surface.
///
/// Claim resolution order (mirrors <see cref="Controllers.HolonController"/> +
/// <see cref="Core.ApiKeyAuthenticationHandler"/>):
///   1. <see cref="ClaimTypes.NameIdentifier"/> — set by JWT (sub → NameIdentifier
///      via ASP.NET's default JWT claim mapping) and by ApiKeyAuthenticationHandler.
///   2. "sub" — explicit copy emitted by ApiKeyAuthenticationHandler + TestAuthHandler.
///   3. "AvatarId" — additional explicit copy emitted by ApiKeyAuthenticationHandler
///      for callers that inspect the custom claim directly.
/// </summary>
public sealed class McpAuthMiddleware
{
    private readonly RequestDelegate _next;

    public McpAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        // Only enforce on /mcp paths.
        if (!ctx.Request.Path.StartsWithSegments("/mcp"))
        {
            await _next(ctx);
            return;
        }

        // The auth pipeline (JWT or ApiKey) has already populated ctx.User by the
        // time this middleware runs (placed after UseAuthentication/UseAuthorization
        // in Program.cs's pipeline).
        //
        // Resolution priority:
        //   ClaimTypes.NameIdentifier covers both JWT (default claim mapping from
        //   "sub") and the ApiKey scheme's explicit copy.
        //   "sub" is an explicit copy emitted by ApiKeyAuthenticationHandler and
        //   TestAuthHandler — acts as a belt-and-suspenders fallback.
        //   "AvatarId" is the additional explicit ApiKey-scheme copy; checked last
        //   because it is only present under the ApiKey scheme, not JWT.
        var avatarClaim =
            ctx.User?.FindFirst(ClaimTypes.NameIdentifier)
            ?? ctx.User?.FindFirst("sub")
            ?? ctx.User?.FindFirst("AvatarId");

        if (avatarClaim is null || !Guid.TryParse(avatarClaim.Value, out var avatarId))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(
                "{\"error\":\"mcp_unauthorized\",\"detail\":\"missing or invalid avatar_id claim\"}");
            return;
        }

        // Stash on the request items bag so the MCP dispatcher (McpToolRegistry or
        // any custom tool-call handler) can build a ToolCallContext.AvatarId.
        ctx.Items["mcp.avatar_id"] = avatarId;
        await _next(ctx);
    }
}
