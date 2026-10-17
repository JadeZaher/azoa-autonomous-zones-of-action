using AZOA.WebAPI.Models;
using AZOA.WebAPI.Services.Webhooks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/tenant/webhook-registration")]
[Authorize]
[EnableRateLimiting("financial")]
[RequestSizeLimit(16384)]
public sealed class WebhookRegistrationController(WebhookRegistrationManagement management) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await management.GetAsync(User, null, ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPut]
    public async Task<IActionResult> Put(WebhookRegistrationCommand command, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await management.MutateAsync(User, null, command, ct);
        return result.IsError ? Conflict(result) : Ok(result);
    }
    [HttpPost("withdraw")]
    public async Task<IActionResult> Withdraw(WebhookRegistrationCommand command, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await management.WithdrawAsync(User, null, command, ct);
        return result.IsError ? Conflict(result) : Ok(result);
    }
}
