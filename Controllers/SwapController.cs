using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]  // Optional: quotas via avatar
public class SwapController : ControllerBase
{
    private readonly ISwapManager _swapManager;

    public SwapController(ISwapManager swapManager)
    {
        _swapManager = swapManager;
    }

    [HttpGet("quote")]
    public async Task<ActionResult<AZOAResult<SwapQuoteResponse>>> GetQuote([FromQuery] SwapQuoteRequest request)
    {
        var result = await _swapManager.GetQuoteAsync(request);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("execute")]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<SwapQuoteResponse>>> ExecuteSwap([FromBody] SwapExecuteRequest request)
    {
        if (!HasSigningScope(AzoaScopes.SwapSign))
            return StatusCode(StatusCodes.Status403Forbidden, new AZOAResult<SwapQuoteResponse>
            {
                IsError = true,
                Message = $"Caller lacks the '{AzoaScopes.SwapSign}' scope required to execute a swap."
            });

        // Optional client Idempotency-Key. Accepted + plumbed through; the swap
        // path returns an UNSIGNED tx (client signs + broadcasts) so there is no
        // server-side irreversible effect to dedupe. Absent ⇒ null (no random
        // key generated).
        var idempotencyKey = ReadIdempotencyKey();

        var result = await _swapManager.GetSwapTransactionAsync(request, idempotencyKey);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// Reads the optional client <c>Idempotency-Key</c> request header.
    /// Returns null when absent/blank (server falls back to its deterministic
    /// content key downstream; never a random per-request key).
    /// </summary>
    private string? ReadIdempotencyKey()
    {
        if (Request.Headers.TryGetValue("Idempotency-Key", out var values))
        {
            var key = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(key))
                return key.Trim();
        }
        return null;
    }

    /// <summary>
    /// True iff the caller may perform a <paramref name="scope"/>-gated signing action.
    /// Mirrors the DappDevelop policy's "empty CSV = legacy full access" rule so a
    /// scoped API key is RESTRICTED without locking out JWT owners or full-access keys.
    /// See Controllers/AGENTS.md §per-endpoint-signing-scope.
    /// </summary>
    private bool HasSigningScope(string scope)
    {
        var isApiKey = string.Equals(User.FindFirst("AuthMethod")?.Value, "ApiKey", StringComparison.OrdinalIgnoreCase);
        if (!isApiKey) return true;                                        // JWT owner → unaffected.
        if (string.Equals(User.FindFirst("ScopesRestricted")?.Value, "true", StringComparison.OrdinalIgnoreCase))
            return false;                                                 // all-forbidden CSV → not full access.
        if (User.GetScopes().Count == 0) return true;                     // empty CSV → legacy full access.
        return User.HasScope(scope);                                      // scoped key → must carry the scope.
    }
}
