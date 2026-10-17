using System.Text;
using AZOA.WebAPI.Services.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

/// <summary>Authenticates versioned identity callbacks before authoritative recovery.</summary>
[ApiController]
[Route("api/kyc/callback")]
[AllowAnonymous]
[EnableRateLimiting(KycCallbackController.RateLimitPolicy)]
public sealed class HostedIdentityCallbackController(HostedIdentityRecovery recovery) : ControllerBase
{
    [HttpPost("veriff")]
    [HttpPost("generic-hosted")]
    [RequestSizeLimit(65536)]
    [Consumes("application/json")]
    public async Task<IActionResult> Callback(CancellationToken ct)
    {
        if (Request.ContentLength > 65536) return Accepted();
        var bytes = new byte[65537];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await Request.Body.ReadAsync(bytes.AsMemory(length), ct);
            if (read == 0) break;
            length += read;
        }
        if (length > 65536) return Accepted();
        string body;
        try { body = new UTF8Encoding(false, true).GetString(bytes, 0, length); }
        catch (DecoderFallbackException) { return Accepted(); }
        var headers = Request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var adapter = Request.Path.Value?.EndsWith("/veriff", StringComparison.OrdinalIgnoreCase) == true ? "veriff" : "generic-hosted";
        var result = await recovery.CallbackAsync(adapter, body, headers, ct);
        return result == HostedIdentityCallbackDisposition.Retry ? StatusCode(503) : Accepted();
    }
}
