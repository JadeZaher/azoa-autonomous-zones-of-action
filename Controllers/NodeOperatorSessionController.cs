using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/operator/session")]
[Authorize(Policy = "NodeOperatorSession")]
public sealed class NodeOperatorSessionController : ControllerBase
{
    private readonly INodeOperatorManager _manager;

    public NodeOperatorSessionController(INodeOperatorManager manager)
    {
        _manager = manager;
    }

    [HttpPost]
    [AllowAnonymous]
    [CredentialFreePublicEndpoint]
    [EnableRateLimiting("operator-login")]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<AZOAResult<NodeOperatorSessionResponse>>> Login(
        [FromBody] NodeOperatorLoginRequest request,
        CancellationToken ct)
    {
        var clientAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await _manager.LoginAsync(request, clientAddress, ct);
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        if (!result.IsError)
            return Ok(result);
        if (string.Equals(result.Code, NodeOperatorErrorCodes.LoginThrottled, StringComparison.Ordinal))
        {
            Response.Headers.RetryAfter = (result.RetryAfterSeconds ?? 1).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status429TooManyRequests, result);
        }
        if (string.Equals(result.Code, NodeOperatorErrorCodes.ServiceUnavailable, StringComparison.Ordinal))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, result);
        return Unauthorized(result);
    }

    [HttpPost("revoke")]
    [Authorize(Policy = "RecentNodeOperatorSession")]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<bool>>> Revoke(CancellationToken ct)
    {
        var result = await _manager.RevokeAllSessionsAsync(ct);
        return result.IsError ? StatusCode(StatusCodes.Status503ServiceUnavailable, result) : Ok(result);
    }
}
