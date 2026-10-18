using System.Globalization;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.NodeAdmin;
using Microsoft.AspNetCore.Mvc;

namespace AZOA.WebAPI.Controllers;

/// <summary>Shared result mapping for <c>api/node-admin/*</c>: AZOAResult envelope, contract status codes, no-store.</summary>
public abstract class NodeAdminControllerBase : ControllerBase
{
    /// <summary>The validated caller; the NodeAdmin policy guarantees it on every non-anonymous action.</summary>
    protected NodeAdminCaller Caller => NodeAdminCaller.From(HttpContext)
        ?? throw new InvalidOperationException("Node-admin action reached without a validated node-admin caller.");

    protected ActionResult<AZOAResult<T>> Respond<T>(AZOAResult<T> result)
    {
        Response.Headers.CacheControl = "no-store";
        if (!result.IsError) return Ok(result);
        if (result.RetryAfterSeconds is { } retryAfter)
            Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        if (result.Code == NodeAdminErrorCodes.StepUpRequired)
            Response.Headers[NodeAdminErrorCodes.AuthRequirementHeader] = NodeAdminErrorCodes.RecentNodeAdminLogin;
        return StatusCode(NodeAdminResults.StatusFor(result.Code), result);
    }
}
