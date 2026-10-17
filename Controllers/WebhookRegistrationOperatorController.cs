using AZOA.WebAPI.Models;
using AZOA.WebAPI.Services.Webhooks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/operator/tenants/{tenantId:guid}/webhook-registration")]
[Authorize(Policy = "RecentNodeOperatorSession")]
[EnableRateLimiting("financial")]
[RequestSizeLimit(16384)]
public sealed class WebhookRegistrationOperatorController(WebhookRegistrationManagement management) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid tenantId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await management.GetAsync(User, tenantId, ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPut]
    public async Task<IActionResult> Put(Guid tenantId, WebhookRegistrationCommand command, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await management.MutateAsync(User, tenantId, command, ct);
        return result.IsError ? Conflict(result) : Ok(result);
    }
    [HttpPost("withdraw")]
    public async Task<IActionResult> Withdraw(Guid tenantId, WebhookRegistrationCommand command, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await management.WithdrawAsync(User, tenantId, command, ct);
        return result.IsError ? Conflict(result) : Ok(result);
    }
}
