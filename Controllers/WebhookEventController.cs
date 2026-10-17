using AZOA.WebAPI.Models;
using AZOA.WebAPI.Services.Webhooks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/tenant/webhook-events")]
[Authorize]
[EnableRateLimiting("financial")]
[RequestSizeLimit(16384)]
public sealed class WebhookEventController(WebhookEventManagement events) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int offset = 0, [FromQuery] int limit = 25, CancellationToken ct = default)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await events.ListAsync(User, null, offset, limit, ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpGet("{eventId:guid}")]
    public async Task<IActionResult> Get(Guid eventId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await events.GetAsync(User, null, eventId, ct);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    [HttpPost("{eventId:guid}/retry")]
    public async Task<IActionResult> Retry(Guid eventId, WebhookEventRetryCommand command, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await events.RetryAsync(User, null, eventId, command, ct);
        return result.IsError ? Conflict(result) : Ok(result);
    }
}
