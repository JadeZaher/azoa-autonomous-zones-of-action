// SPDX-License-Identifier: UNLICENSED

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

/// <summary>Authenticates wallet proofs and transfers account credential ownership.</summary>
[ApiController]
[Route("api/user")]
public class WalletAuthController : ControllerBase
{
    private readonly IWalletAuthManager _manager;

    public WalletAuthController(IWalletAuthManager manager)
    {
        _manager = manager;
    }

    /// <summary>AC1: issue a one-time, single-use, ≤5-min challenge bound to (address, chainType).</summary>
    [HttpPost("auth/challenge")]
    [AllowAnonymous]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<WalletChallengeResponse>>> Challenge([FromBody] WalletChallengeRequest request)
    {
        var result = await _manager.CreateChallengeAsync(request.Address, request.ChainType, HttpContext.RequestAborted);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>AC1/AC2: verify the signature and return the login JWT (create-or-login only).</summary>
    [HttpPost("auth/verify")]
    [AllowAnonymous]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<WalletAuthTokenResponse>>> Verify([FromBody] WalletVerifyRequest request)
    {
        var result = await _manager.VerifyAsync(
            request.Address, request.ChainType, request.Signature, request.Message, HttpContext.RequestAborted);
        // An auth failure surfaces as 401 (never 400) so a caller can't distinguish
        // "no challenge" from "bad signature" beyond the generic message.
        if (result.IsError) return Unauthorized(result);
        return Ok(result);
    }

    /// <summary>AC2b: bind a wallet to the ALREADY-AUTHENTICATED user (authed AS that account).</summary>
    [HttpPost("wallet/link")]
    [Authorize(Policy = "RecentFirstPartyLogin")]
    public async Task<ActionResult<AZOAResult<bool>>> LinkWallet([FromBody] WalletLinkRequest request)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized();

        var result = await _manager.LinkWalletAsync(
            userId.Value, request.Address, request.ChainType, request.Signature, request.Message,
            HttpContext.RequestAborted);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>AC4: tenant mints a single-use claim invite for a child it owns.</summary>
    [HttpPost("claim-invite")]
    [Authorize(Policy = "TenantScope")]
    public async Task<ActionResult<AZOAResult<ClaimInviteResponse>>> CreateClaimInvite([FromBody] CreateClaimInviteRequest request)
    {
        var tenantId = GetUserIdFromClaims();
        if (tenantId is null) return Unauthorized();

        var result = await _manager.CreateClaimInviteAsync(tenantId.Value, request.ChildUserId, HttpContext.RequestAborted);
        return TranslateResult(result);
    }

    /// <summary>Claims an invited identity with a user-side credential.</summary>
    [HttpPost("claim")]
    [AllowAnonymous]
    public async Task<ActionResult<AZOAResult<WalletAuthTokenResponse>>> Claim([FromBody] ClaimUserRequest request)
    {
        var result = await _manager.ClaimAsync(
            request.TargetUserId,
            request.ClaimToken,
            request.NewPassword,
            request.Address,
            request.ChainType,
            request.Signature,
            request.Message,
            HttpContext.RequestAborted);

        return TranslateResult(result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a manager result to the right HTTP status, mirroring
    /// <c>TenantController.TranslateResult</c>: a
    /// <see cref="TenantAuthorizationError.NotFound"/>-prefixed message → 404 (the
    /// cross-tenant isolation crux), any other error → 400.
    /// </summary>
    private ActionResult<AZOAResult<T>> TranslateResult<T>(AZOAResult<T> result)
    {
        if (!result.IsError) return Ok(result);

        if (result.Message?.StartsWith(TenantAuthorizationError.NotFound, StringComparison.Ordinal) == true)
            return NotFound(result);

        return BadRequest(result);
    }

    /// <summary>
    /// The user/tenant id is ALWAYS the authenticated principal's subject — never a
    /// request body field (IDOR rule). Mirrors <c>UserController.GetUserIdFromClaims</c>.
    /// Returns null when the request is unauthenticated.
    /// </summary>
    private Guid? GetUserIdFromClaims()
    {
        var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User?.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
