using AZOA.WebAPI.Interfaces.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

/// <summary>Opaque authenticated trigger for authoritative business verification refresh.</summary>
[ApiController]
[Route("api/kyb/identomat/callback")]
[AllowAnonymous]
[EnableRateLimiting(KycCallbackController.RateLimitPolicy)]
public sealed class IdentomatKybCallbackController(IKybService service) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var buffer = new char[65536];
        var length = await reader.ReadBlockAsync(buffer, ct);
        if (length is > 0 and < 65536) await service.HandleCallbackAsync(new string(buffer, 0, length), ct);
        return Accepted(new { accepted = true });
    }
}
