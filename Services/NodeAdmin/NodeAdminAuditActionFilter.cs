using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>Request metadata recorded beside (not inside) the audit hash.</summary>
public sealed record NodeAdminRequestInfo(string Ip, string UserAgent, string RequestId)
{
    public static NodeAdminRequestInfo From(HttpContext context)
    {
        var agent = context.Request.Headers.UserAgent.ToString();
        return new(
            context.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            agent.Length > 256 ? agent[..256] : agent,
            context.TraceIdentifier);
    }
}

/// <summary>Audits node-admin writes and personal/financial reads; see <c>Services/NodeAdmin/AGENTS.md</c> §audit.</summary>
public sealed class NodeAdminAuditActionFilter(NodeAdminAuditLog audit, ILogger<NodeAdminAuditActionFilter> logger) : IAsyncActionFilter
{
    /// <summary>Runs before <c>ModelStateInvalidFilter</c> (order -2000), so invalid-model 400s get an outcome row.</summary>
    public const int FilterOrder = -3000;

    private const int MaxQueryNames = 32;
    private const int MaxQueryNameLength = 64;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var caller = NodeAdminCaller.From(http);
        if (caller is null)
        {
            await next();
            return;
        }
        if (NodeAdminRoutePermissions.IsReadRequest(http.GetEndpoint(), http.Request.Method))
        {
            await AuditedReadAsync(context, next, caller);
            return;
        }

        var info = NodeAdminRequestInfo.From(http);
        var action = Action(context);
        var target = Target(context);
        try
        {
            await audit.AppendAsync(new NodeAdminAuditEntry(caller.Principal, caller.Amr, action, target,
                NodeAdminAuditLog.OutcomeIntent, string.Empty, info.Ip, info.RequestId), http.RequestAborted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Node-admin audit intent could not be written; refusing {Action}", action);
            context.Result = new ObjectResult(AZOAResult<object>.FailureWithCode(
                "The audit trail is unavailable, so this change was not made.", NodeAdminErrorCodes.AuditUnavailable))
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
            };
            return;
        }

        var executed = await next();
        var status = Status(executed, http);
        var outcome = status is >= 200 and < 300 ? NodeAdminAuditLog.OutcomeOk
            : status == StatusCodes.Status403Forbidden ? NodeAdminAuditLog.OutcomeDenied
            : NodeAdminAuditLog.OutcomeFailed;
        var code = (executed.Result as ObjectResult)?.Value?.GetType().GetProperty(nameof(AZOAResult<object>.Code))
            ?.GetValue((executed.Result as ObjectResult)!.Value) as string;
        var detail = JsonSerializer.Serialize(new { status, code });
        try
        {
            await audit.AppendAsync(new NodeAdminAuditEntry(caller.Principal, caller.Amr, action, target, outcome, detail,
                info.Ip, info.RequestId), CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Node-admin audit outcome could not be written for {Action} ({Outcome})", action, outcome);
        }
    }

    /// <summary>Records a best-effort read row for personal/financial reads.</summary>
    private async Task AuditedReadAsync(ActionExecutingContext context, ActionExecutionDelegate next, NodeAdminCaller caller)
    {
        var http = context.HttpContext;
        if (!NodeAdminRoutePermissions.TryResolve(http.GetEndpoint(), http.Request.Method, out var grant)
            || grant.Permission is null || !NodeAdminRoutePermissions.AuditedReadPermissions.Contains(grant.Permission))
        {
            await next();
            return;
        }

        var executed = await next();
        var info = NodeAdminRequestInfo.From(http);
        var action = Action(context);
        var detail = JsonSerializer.Serialize(new { status = Status(executed, http), permission = grant.Permission, query = QueryShape(http) });
        try
        {
            await audit.AppendAsync(new NodeAdminAuditEntry(caller.Principal, caller.Amr, action, Target(context),
                NodeAdminAuditLog.OutcomeRead, detail, info.Ip, info.RequestId), CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogCritical(exception, "NodeAdminReadAuditUnavailable actor={Actor} action={Action} requestId={RequestId}",
                caller.Principal, action, info.RequestId);
        }
    }

    private static string Action(ActionExecutingContext context)
        => $"{context.HttpContext.Request.Method} /{context.ActionDescriptor.AttributeRouteInfo?.Template ?? context.HttpContext.Request.Path.Value?.TrimStart('/')}";

    private static int Status(ActionExecutedContext executed, HttpContext http)
        => executed.Exception is not null && !executed.ExceptionHandled
            ? StatusCodes.Status500InternalServerError
            : executed.Result switch
            {
                ObjectResult objectResult => objectResult.StatusCode ?? StatusCodes.Status200OK,
                IStatusCodeActionResult statusResult => statusResult.StatusCode ?? StatusCodes.Status200OK,
                _ => http.Response.StatusCode,
            };

    /// <summary>Sorted query parameter names only: values (emails, names, searched ids) never reach the chain.</summary>
    private static string[] QueryShape(HttpContext http)
        => http.Request.Query.Keys
            .Select(name => name.Length > MaxQueryNameLength ? name[..MaxQueryNameLength] : name)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(MaxQueryNames).ToArray();

    /// <summary>Route values that identify the target (ids, fingerprints, sids), in route order.</summary>
    private static string Target(ActionExecutingContext context)
        => string.Join(',', context.RouteData.Values
            .Where(pair => pair.Key is not ("controller" or "action") && pair.Value is not null)
            .Select(pair => $"{pair.Key}={pair.Value}"));
}
