using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// The USER-scoped consent surface (tenant-consent-delegation §1). Every action
/// is authed as the USER (a plain JWT) and the grantor is sourced EXCLUSIVELY
/// from the token subject — never from a request body (AC9 IDOR). The ArdaNova
/// join flow calls <c>POST</c> here with the USER's own token (the brand-level UX
/// hides the crypto; the user's credential is what authorizes the grant — H4).
///
/// A cross-user probe (revoke/get a grant the user does not own) is reported as
/// 404 via the <see cref="TenantAuthorizationError.NotFound"/> prefix — never
/// 403 — so it is indistinguishable from "no such grant".
/// </summary>
[ApiController]
[Route("api/avatar/consent")]
[Authorize]
public class ConsentController : ControllerBase
{
    private readonly IConsentManager _manager;

    public ConsentController(IConsentManager manager)
    {
        _manager = manager;
    }

    /// <summary>
    /// Grant a tenant scopes on behalf of the authenticated user. The body names
    /// the tenant and scopes; the GRANTOR is always the token subject (never a
    /// body field). A <c>Participation</c> grant excludes value-signing scopes (H4)
    /// and routes through the participation path so that exclusion is asserted.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<AZOAResult<ConsentGrantResponse>>> Grant([FromBody] GrantConsentRequest request)
    {
        var userId = GetAvatarIdFromClaims();
        if (userId is null) return Unauthorized();

        if (request is null)
            return BadRequest(new AZOAResult<ConsentGrantResponse> { IsError = true, Message = "Request body is required." });

        // Parse the origin string into the enum; default UserExplicit, reject junk.
        if (!TryParseOrigin(request.Origin, out var origin))
            return BadRequest(new AZOAResult<ConsentGrantResponse>
            {
                IsError = true,
                Message = "Unrecognized origin; expected 'UserExplicit' or 'Participation'.",
            });

        AZOAResult<ConsentGrant> result;
        if (origin == GrantOrigin.Participation)
        {
            // Participation grants flow through the dedicated path: required ref +
            // value-scope exclusion are enforced there. Still authed as the USER.
            result = await _manager.GrantParticipationAsync(
                userId.Value, request.TenantId, request.ParticipationRef ?? string.Empty,
                request.Scopes ?? new List<string>(), HttpContext.RequestAborted);
        }
        else
        {
            result = await _manager.GrantAsync(
                userId.Value, request.TenantId, request.Scopes ?? new List<string>(),
                GrantOrigin.UserExplicit, request.ParticipationRef, request.ExpiresAt,
                HttpContext.RequestAborted);
        }

        return TranslateGrant(result);
    }

    /// <summary>Revoke one of the user's own grants (404 if not owned — AC9).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<AZOAResult<bool>>> Revoke(Guid id)
    {
        var userId = GetAvatarIdFromClaims();
        if (userId is null) return Unauthorized();

        var result = await _manager.RevokeAsync(userId.Value, id, HttpContext.RequestAborted);
        if (result.IsError)
            return IsNotFound(result.Message) ? NotFound(result) : BadRequest(result);

        return Ok(result);
    }

    /// <summary>List the authenticated user's own grants (AC1).</summary>
    [HttpGet]
    public async Task<ActionResult<AZOAResult<IEnumerable<ConsentGrantResponse>>>> List()
    {
        var userId = GetAvatarIdFromClaims();
        if (userId is null) return Unauthorized();

        var result = await _manager.ListForUserAsync(userId.Value, HttpContext.RequestAborted);
        if (result.IsError) return BadRequest(result);

        return Ok(ToResponseList(result));
    }

    /// <summary>Status query for one of the user's own grants (404 if not owned — AC9).</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AZOAResult<ConsentGrantResponse>>> Get(Guid id)
    {
        var userId = GetAvatarIdFromClaims();
        if (userId is null) return Unauthorized();

        var result = await _manager.GetForUserAsync(userId.Value, id, HttpContext.RequestAborted);
        return TranslateGrant(result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a single-grant manager result to the right status, projecting the
    /// domain grant onto the narrow <see cref="ConsentGrantResponse"/>. A
    /// <see cref="TenantAuthorizationError.NotFound"/>-prefixed message → 404; any
    /// other error → 400 (mirrors <c>TenantController.TranslateResult</c>).
    /// </summary>
    private ActionResult<AZOAResult<ConsentGrantResponse>> TranslateGrant(AZOAResult<ConsentGrant> result)
    {
        if (result.IsError)
        {
            var mapped = new AZOAResult<ConsentGrantResponse> { IsError = true, Message = result.Message, Exception = result.Exception };
            return IsNotFound(result.Message) ? NotFound(mapped) : BadRequest(mapped);
        }

        return Ok(new AZOAResult<ConsentGrantResponse>
        {
            Result = ToResponse(result.Result!),
            Message = result.Message,
        });
    }

    private static AZOAResult<IEnumerable<ConsentGrantResponse>> ToResponseList(AZOAResult<IEnumerable<ConsentGrant>> result)
        => new()
        {
            Result = (result.Result ?? Enumerable.Empty<ConsentGrant>()).Select(ToResponse).ToList(),
            Message = result.Message,
        };

    private static ConsentGrantResponse ToResponse(ConsentGrant g) => new()
    {
        GrantId = g.Id,
        TenantId = g.TenantId,
        Scopes = g.Scopes,
        Origin = g.Origin.ToString(),
        ParticipationRef = g.ParticipationRef,
        GrantedAt = g.GrantedAt,
        ExpiresAt = g.ExpiresAt,
        RevokedAt = g.RevokedAt,
    };

    private static bool IsNotFound(string? message)
        => message?.StartsWith(TenantAuthorizationError.NotFound, StringComparison.Ordinal) == true;

    /// <summary>Parses the optional origin string; null/empty → UserExplicit.</summary>
    private static bool TryParseOrigin(string? raw, out GrantOrigin origin)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            origin = GrantOrigin.UserExplicit;
            return true;
        }

        return Enum.TryParse(raw.Trim(), ignoreCase: true, out origin)
               && Enum.IsDefined(typeof(GrantOrigin), origin);
    }

    /// <summary>
    /// The grantor is the authenticated user's avatar id — ALWAYS from the token
    /// subject claim, never from a request body (AC9). Mirrors
    /// <c>AvatarController.GetAvatarIdFromClaims</c>.
    /// </summary>
    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User?.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
