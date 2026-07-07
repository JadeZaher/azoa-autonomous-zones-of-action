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
            await context.Response.WriteAsJsonAsync(new
            {
                isError = true,
                message = $"This API key lacks the '{AzoaScopes.DappDevelop}' scope required to author "
                        + "holons/quests/dApp-series. Rotate the key with that scope.",
            });
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
