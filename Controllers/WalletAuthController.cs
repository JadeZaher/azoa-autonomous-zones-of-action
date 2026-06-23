// SPDX-License-Identifier: UNLICENSED

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// Self-sovereign wallet-challenge authentication + claim surface
/// (user-sovereign-identity §1/§2). Routes under <c>api/avatar</c>:
/// <list type="bullet">
///   <item><c>POST auth/challenge</c> — issue a one-time nonce to sign (anonymous,
///   per-IP rate-limited via the <c>financial</c> policy; AC1).</item>
///   <item><c>POST auth/verify</c> — verify the signature → standard login JWT;
///   create-or-login only (anonymous; AC1/AC2/AC2b).</item>
///   <item><c>POST wallet/link</c> — bind a wallet to the AUTHENTICATED avatar
///   (authed AS that account; AC2b).</item>
///   <item><c>POST claim-invite</c> — tenant mints a single-use claim token for a
///   child it owns (TenantScope; AC4).</item>
///   <item><c>POST claim</c> — user takes ownership with a user-side credential
///   (claim token OR authenticated child-JWT subject; AC3/AC3b/AC4).</item>
/// </list>
///
/// IDOR rule (mirrors <c>AvatarController</c>/<c>TenantController</c>): every owned
/// identity id is taken from the authenticated principal or a single-use token, never
/// a request body field.
/// </summary>
[ApiController]
[Route("api/avatar")]
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

    /// <summary>AC2b: bind a wallet to the ALREADY-AUTHENTICATED avatar (authed AS that account).</summary>
    [HttpPost("wallet/link")]
    [Authorize]
    public async Task<ActionResult<AZOAResult<bool>>> LinkWallet([FromBody] WalletLinkRequest request)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId is null) return Unauthorized();

        var result = await _manager.LinkWalletAsync(
            avatarId.Value, request.Address, request.ChainType, request.Signature, request.Message,
            HttpContext.RequestAborted);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>AC4: tenant mints a single-use claim invite for a child it owns.</summary>
    [HttpPost("claim-invite")]
    [Authorize(Policy = "TenantScope")]
    public async Task<ActionResult<AZOAResult<ClaimInviteResponse>>> CreateClaimInvite([FromBody] CreateClaimInviteRequest request)
    {
        var tenantId = GetAvatarIdFromClaims();
        if (tenantId is null) return Unauthorized();

        var result = await _manager.CreateClaimInviteAsync(tenantId.Value, request.ChildAvatarId, HttpContext.RequestAborted);
        return TranslateResult(result);
    }

    /// <summary>
    /// AC3/AC3b/AC4: claim a tenant-provisioned avatar with a USER-SIDE credential.
    /// Target id comes from a single-use claim token OR the authenticated child-JWT
    /// subject — NEVER a body field. Anonymous is permitted ONLY with a claim token.
    /// </summary>
    [HttpPost("claim")]
    [AllowAnonymous]
    public async Task<ActionResult<AZOAResult<WalletAuthTokenResponse>>> Claim([FromBody] ClaimAvatarRequest request)
    {
        // Read the authenticated subject if a JWT was presented; absent for a
        // token-only claim. The manager rejects when NEITHER is available.
        var authedAvatarId = GetAvatarIdFromClaims();

        var result = await _manager.ClaimAsync(
            authedAvatarId,
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
    /// The avatar/tenant id is ALWAYS the authenticated principal's subject — never a
    /// request body field (IDOR rule). Mirrors <c>AvatarController.GetAvatarIdFromClaims</c>.
    /// Returns null when the request is unauthenticated.
    /// </summary>
    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User?.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
