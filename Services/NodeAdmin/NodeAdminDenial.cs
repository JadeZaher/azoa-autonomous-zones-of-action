using System.Text.Json;
using AZOA.WebAPI.Core;
using Microsoft.AspNetCore.Authorization.Policy;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>Writes the contract 403 for a node-admin authorization failure and records the denial in the audit chain (H4).</summary>
public static class NodeAdminDenial
{
    public static async Task WriteAsync(HttpContext context, PolicyAuthorizationResult result)
    {
        var reasons = result.AuthorizationFailure?.FailureReasons.Select(reason => reason.Message).ToHashSet(StringComparer.Ordinal) ?? [];
        var code = !reasons.Contains(NodeAdminErrorCodes.PermissionDenied) && reasons.Contains(NodeAdminErrorCodes.StepUpRequired)
            ? NodeAdminErrorCodes.StepUpRequired
            : NodeAdminErrorCodes.PermissionDenied;
        var caller = NodeAdminCaller.From(context);
        try
        {
            var audit = context.RequestServices.GetRequiredService<NodeAdminAuditLog>();
            var info = NodeAdminRequestInfo.From(context);
            await audit.AppendAsync(new NodeAdminAuditEntry(
                caller?.Principal ?? NodeAdminClaims.Find(context.User, NodeAdminClaims.Subject) ?? "anonymous",
                caller?.Amr ?? [],
                "authz.denied",
                $"{context.Request.Method} {context.Request.Path}",
                NodeAdminAuditLog.OutcomeDenied,
                JsonSerializer.Serialize(new { code }),
                info.Ip,
                info.RequestId), context.RequestAborted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(NodeAdminDenial))
                .LogError(exception, "Node-admin denial could not be audited for {Path}", context.Request.Path.Value);
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        if (code == NodeAdminErrorCodes.StepUpRequired)
            context.Response.Headers[NodeAdminErrorCodes.AuthRequirementHeader] = NodeAdminErrorCodes.RecentNodeAdminLogin;
        await context.Response.WriteAsJsonAsync(new
        {
            isError = true,
            code,
            message = code == NodeAdminErrorCodes.StepUpRequired
                ? "Sign in again to confirm this sensitive node-admin action."
                : "Your node-admin roles do not grant this action.",
        });
    }
}
