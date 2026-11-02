using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SurrealForge.Client;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/tenant/custodial-accounts/{externalSubject}/handoff")]
[Authorize(Policy = "TenantScope")]
[EnableRateLimiting("financial")]
[RequestSizeLimit(4096)]
public sealed class MemberHandoffController(MemberHandoffService handoffs) : ControllerBase
{
    [HttpPost]
    public Task<IActionResult> Create(string externalSubject, MemberHandoffRequest request, CancellationToken ct)
        => Issue(externalSubject, request, null, ct);

    [HttpPost("{requestId:guid}/resend")]
    public Task<IActionResult> Resend(string externalSubject, Guid requestId, MemberHandoffRequest request, CancellationToken ct)
        => Issue(externalSubject, request, requestId, ct);

    [HttpGet("{requestId:guid}")]
    public async Task<IActionResult> Get(string externalSubject, Guid requestId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await handoffs.TenantReadAsync(User, externalSubject, requestId, ct);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    private async Task<IActionResult> Issue(string external, MemberHandoffRequest request, Guid? resendId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var result = await handoffs.IssueAsync(User, external, request, resendId, ct);
            return !result.IsError ? Ok(result) : StatusCode(result.Code switch
            {
                AzoaErrorCodes.NotFound => 404, AzoaErrorCodes.Forbidden => 403,
                AzoaErrorCodes.Conflict => 409, "RAIL_BUSY" => 429, "PAYMENTS_NOT_CONFIGURED" => 503, _ => 400,
            }, result);
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains("HANDOFF_", StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
        {
            return Conflict(new { isError = true, code = "CONFLICT", message = "The account or invitation changed. Retry this handoff." });
        }
    }
}

[ApiController]
[Route("api/user/handoffs")]
[Authorize(Policy = "FirstPartyLogin")]
[EnableRateLimiting("financial")]
[RequestSizeLimit(4096)]
public sealed class MemberHandoffUserController(MemberHandoffService handoffs) : ControllerBase
{
    [HttpPost("preview")]
    [AllowAnonymous]
    public async Task<IActionResult> Preview(MemberHandoffPreview request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await handoffs.PreviewAsync(request, ct);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    [HttpGet("{requestId:guid}")]
    public async Task<IActionResult> Get(Guid requestId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await handoffs.ReadAsync(User, requestId, ct);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    [HttpPost("{requestId:guid}/approve")]
    public Task<IActionResult> Approve(Guid requestId, CancellationToken ct) => Decide(requestId, true, ct);

    [HttpPost("{requestId:guid}/decline")]
    public Task<IActionResult> Decline(Guid requestId, CancellationToken ct) => Decide(requestId, false, ct);

    private async Task<IActionResult> Decide(Guid id, bool approve, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await handoffs.DecideAsync(User, id, approve, ct);
        return result.IsError ? Conflict(result) : Ok(result);
    }
}

[ApiController]
[Route("api/operator/member-handoff-deliveries")]
[Authorize(Policy = "RecentNodeOperatorSession")]
[EnableRateLimiting("financial")]
public sealed class MemberHandoffDeliveryController(MemberHandoffService handoffs) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new { isError = false, result = await handoffs.DeliveriesAsync(ct) });
    }

    [HttpGet("{requestId:guid}")]
    public async Task<IActionResult> Get(Guid requestId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var delivery = await handoffs.DeliveryAsync(requestId, ct);
        return delivery is null ? NotFound() : Ok(new { isError = false, result = delivery });
    }
}
