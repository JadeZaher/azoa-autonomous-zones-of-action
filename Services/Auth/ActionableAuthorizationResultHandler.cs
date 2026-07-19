using AZOA.WebAPI.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace AZOA.WebAPI.Services.Auth;

/// <summary>Returns stable, actionable authorization failures for scope and step-up gates.</summary>
public sealed class ActionableAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden
            && context.User.Identity?.IsAuthenticated == true
            && !context.Response.HasStarted
            && TryGetRecentLoginRequirement(context, out var code, out var requirement, out var stepUpMessage))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-AZOA-Auth-Requirement"] = requirement;
            await context.Response.WriteAsJsonAsync(new
            {
                isError = true,
                code,
                message = stepUpMessage,
            });
            return;
        }

        var isApiKey = string.Equals(
            context.User.FindFirst("AuthMethod")?.Value,
            "ApiKey",
            StringComparison.OrdinalIgnoreCase);
        if (authorizeResult.Forbidden
            && context.User.Identity?.IsAuthenticated == true
            && isApiKey
            && !context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            var missingScope = RequiredScope(context);
            var message = missingScope is AzoaScopes.DappDevelop or AzoaScopes.DappManage
                ? $"This API key lacks the '{missingScope}' scope or owning-avatar role required "
                  + "for this dApp operation. Rotate the key after updating the avatar role."
                : $"This API key cannot satisfy the '{missingScope}' policy. Use a JWT identity "
                  + "that carries the required capability.";
            await context.Response.WriteAsJsonAsync(new
            {
                isError = true,
                message,
            });
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }

    private static bool TryGetRecentLoginRequirement(
        HttpContext context,
        out string code,
        out string requirement,
        out string message)
    {
        var policies = context.GetEndpoint()?.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Select(data => data.Policy)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        if ((policies.Contains("RecentNodeOperatorSession")
                || policies.Contains("DappRoleAssignment"))
            && AzoaClaims.IsNodeOperator(context.User))
        {
            code = "RECENT_OPERATOR_LOGIN_REQUIRED";
            requirement = "recent-operator-login";
            message = "Sign in to the node operator console again to confirm this sensitive action.";
            return true;
        }
        if (policies.Contains("RecentFirstPartyLogin")
            && string.Equals(
                context.User.FindFirst(AzoaClaims.TokenUse)?.Value,
                AzoaClaims.TokenUseLogin,
                StringComparison.Ordinal))
        {
            code = "RECENT_LOGIN_REQUIRED";
            requirement = "recent-first-party-login";
            message = "Sign in again to confirm this sensitive account action.";
            return true;
        }

        code = string.Empty;
        requirement = string.Empty;
        message = string.Empty;
        return false;
    }

    private static string RequiredScope(HttpContext context)
    {
        var policies = context.GetEndpoint()?.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Select(data => data.Policy)
            .ToArray()
            ?? Array.Empty<string?>();
        if (policies.Contains("NodeGovern", StringComparer.Ordinal))
            return AzoaScopes.NodeGovern;
        if (policies.Contains("Operator", StringComparer.Ordinal))
            return AzoaScopes.Operator;
        if (policies.Contains("DappManage", StringComparer.Ordinal))
            return AzoaScopes.DappManage;
        if (policies.Contains("FirstPartyLogin", StringComparer.Ordinal))
            return "first-party-login";
        if (policies.Contains("NodeOperatorSession", StringComparer.Ordinal)
            || policies.Contains("RecentNodeOperatorSession", StringComparer.Ordinal))
            return "node-operator-session";
        return AzoaScopes.DappDevelop;
    }
}
