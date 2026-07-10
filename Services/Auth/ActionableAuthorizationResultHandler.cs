using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Services.Auth;

/// <summary>
/// Turns an otherwise body-less policy 403 into an actionable JSON payload naming the
/// missing scope, so a scoped API-key caller sees WHY it was denied. See Services/Auth/AGENTS.md.
/// </summary>
public sealed class ActionableAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        // Only override a genuine authorization FAILURE (403) for an authenticated
        // API-key principal — a 401 (not authenticated) and JWT users keep default behavior.
        var isApiKey = string.Equals(
            context.User.FindFirst("AuthMethod")?.Value, "ApiKey", StringComparison.OrdinalIgnoreCase);

        if (authorizeResult.Forbidden
            && context.User.Identity?.IsAuthenticated == true
            && isApiKey
            && !context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            var missingScope = RequiredDappScope(context);
            await context.Response.WriteAsJsonAsync(new
            {
                isError = true,
                message = $"This API key lacks the '{missingScope}' scope or owning-avatar role required "
                        + "for this dApp operation. Rotate the key after updating the avatar role.",
            });
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }

    private static string RequiredDappScope(HttpContext context)
    {
        var policies = context.GetEndpoint()?
            .Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(a => a.Policy);

        return policies?.Contains("DappManage", StringComparer.Ordinal) == true
            ? AzoaScopes.DappManage
            : AzoaScopes.DappDevelop;
    }
}
