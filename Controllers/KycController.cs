// SPDX-License-Identifier: UNLICENSED

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// Avatar-scoped KYC endpoints. Mirrors <c>STARODKController</c>: the
/// authenticated avatar (claim-sourced) is authoritative; any AvatarId on a
/// request body is ignored, and there is no <c>{avatarId}</c> in any route (so
/// a per-id status lookup IDOR cannot exist — the avatar comes from the token).
/// The manager's <see cref="KycAuthorizationError"/> message prefixes are
/// translated to 403/404 by <see cref="TranslateResult{T}"/>.
///
/// ADMIN AUTHORIZATION (D5): no named admin policy exists in Program.cs today
/// (only a "financial" rate-limit policy + the MultiScheme auth policy). The
/// admin endpoints are therefore gated behind <c>[Authorize]</c> + an explicit
/// admin role/claim check (<see cref="IsAdmin"/>). This is a documented gap:
/// once a first-class admin policy lands, replace the per-action check with
/// <c>[Authorize(Policy = "Admin")]</c>.
/// </summary>
[ApiController]
[Route("api/kyc")]
[Authorize]
public sealed class KycController : ControllerBase
{
    private readonly IKycManager _manager;

    public KycController(IKycManager manager)
    {
        _manager = manager;
    }

    [HttpPost("submit")]
    public async Task<ActionResult<AZOAResult<KycSubmissionModel>>> Submit([FromBody] SubmitKycModel model, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _manager.SubmitAsync(model, avatarId.Value, ct);
        return TranslateResult(result);
    }

    [HttpGet("status")]
    public async Task<ActionResult<AZOAResult<KycSubmissionModel>>> GetStatus(CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _manager.GetStatusAsync(avatarId.Value, ct);
        return TranslateResult(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AZOAResult<KycSubmissionModel>>> GetById(Guid id, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _manager.GetByIdAsync(id, avatarId.Value, ct);
        return TranslateResult(result);
    }

    [HttpGet("{id:guid}/documents")]
    public async Task<ActionResult<AZOAResult<IEnumerable<KycDocumentModel>>>> GetDocuments(Guid id, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        var result = await _manager.ListDocumentsAsync(id, avatarId.Value, ct);
        return TranslateResult(result);
    }

    // ── Admin surface (D5 — per-action admin check, see class remarks) ─────────

    [HttpGet("pending")]
    public async Task<ActionResult<AZOAResult<IEnumerable<KycSubmissionModel>>>> GetPending(CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();

        var result = await _manager.GetPendingAsync(ct);
        return TranslateResult(result);
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<ActionResult<AZOAResult<KycSubmissionModel>>> Approve(Guid id, [FromBody] ReviewKycModel? body, CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();

        var reviewerAvatarId = GetAvatarIdFromClaims();
        if (reviewerAvatarId == null) return Unauthorized();

        var result = await _manager.ApproveAsync(id, reviewerAvatarId.Value, body?.ReviewNotes, ct);
        return TranslateResult(result);
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<ActionResult<AZOAResult<KycSubmissionModel>>> Reject(Guid id, [FromBody] ReviewKycModel? body, CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();

        var reviewerAvatarId = GetAvatarIdFromClaims();
        if (reviewerAvatarId == null) return Unauthorized();

        var result = await _manager.RejectAsync(id, reviewerAvatarId.Value, body?.ReviewNotes, body?.RejectionReason, ct);
        return TranslateResult(result);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps the manager's message-prefix discriminator to the right HTTP status:
    /// Forbidden-prefix → 403, NotFound-prefix → 404, any other error → 400.
    /// </summary>
    private ActionResult<AZOAResult<T>> TranslateResult<T>(AZOAResult<T> result)
    {
        if (!result.IsError) return Ok(result);

        if (result.Message?.StartsWith(KycAuthorizationError.Forbidden, StringComparison.Ordinal) == true)
            return StatusCode(StatusCodes.Status403Forbidden, result);

        if (result.Message?.StartsWith(KycAuthorizationError.NotFound, StringComparison.Ordinal) == true)
            return NotFound(result);

        return BadRequest(result);
    }

    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    /// <summary>
    /// True when the caller carries an admin role/claim. D5 stop-gap until a
    /// first-class admin policy exists in Program.cs.
    /// </summary>
    private bool IsAdmin()
        => User.IsInRole("Admin")
           || string.Equals(User.FindFirst("role")?.Value, "Admin", StringComparison.OrdinalIgnoreCase)
           || string.Equals(User.FindFirst("is_admin")?.Value, "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Review body for the admin approve/reject endpoints. The reviewer
/// id is NEVER read from this body — it comes from the admin's token.</summary>
public sealed class ReviewKycModel
{
    public string? ReviewNotes { get; set; }
    public string? RejectionReason { get; set; }
}
