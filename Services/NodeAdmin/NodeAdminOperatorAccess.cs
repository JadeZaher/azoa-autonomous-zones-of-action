using AZOA.WebAPI.Core;
using Microsoft.AspNetCore.Authorization;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>The node-admin branch of the dual-accepting operator policies; see <c>Services/NodeAdmin/AGENTS.md</c> §route-permission-map.</summary>
public static class NodeAdminOperatorAccess
{
    private static readonly IAuthorizationHandler Reporter = new FailureReporter();

    /// <summary>null for any non-node-admin principal (the legacy predicate decides); otherwise whether the caller's live
    /// permissions satisfy the mapped grant of the current route. Records the denial reason on failure.</summary>
    public static bool? Admit(AuthorizationHandlerContext context)
    {
        if (!NodeAdminClaims.IsNodeAdmin(context.User)) return null;
        var code = Evaluate(context.Resource as HttpContext);
        if (code is null) return true;
        context.Fail(new AuthorizationFailureReason(Reporter, code));
        return false;
    }

    /// <summary>null when admitted; otherwise <c>PERMISSION_DENIED</c> (no caller, unmapped or legacy-only route, missing permission)
    /// or <c>STEP_UP_REQUIRED</c> (mapped recent, session older than 10 minutes).</summary>
    public static string? Evaluate(HttpContext? http)
    {
        var caller = NodeAdminCaller.From(http);
        if (http is null || caller is null
            || !NodeAdminRoutePermissions.TryResolve(http.GetEndpoint(), http.Request.Method, out var grant)
            || grant.Permission is null || !caller.Permissions.Contains(grant.Permission))
            return NodeAdminErrorCodes.PermissionDenied;
        var clock = http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        return grant.RequireRecent && !caller.IsRecent(clock.GetUtcNow()) ? NodeAdminErrorCodes.StepUpRequired : null;
    }

    private sealed class FailureReporter : IAuthorizationHandler
    {
        public Task HandleAsync(AuthorizationHandlerContext context) => Task.CompletedTask;
    }
}
