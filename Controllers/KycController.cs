// SPDX-License-Identifier: UNLICENSED

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
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
    [Authorize(Policy = "FirstPartyLogin")]
    [RequestSizeLimit(KycDocumentRequestLimits.MaxRequestBodyBytes)]
    public async Task<ActionResult<AZOAResult<KycSubmissionModel>>> Submit([FromBody] SubmitKycModel model, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();
        if (User.GetActingTenantId() is not null) return Forbid();

        var result = await _manager.SubmitAsync(model, avatarId.Value, ct);
        return TranslateResult(result);
    }

    [HttpGet("status")]
    [Authorize(Policy = "FirstPartyLogin")]
    public async Task<ActionResult<AZOAResult<KycSubmissionModel>>> GetStatus(CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();
        if (User.GetActingTenantId() is not null) return Forbid();

        var result = await _manager.GetStatusAsync(avatarId.Value, ct);
        return TranslateResult(result);
    }

    /// <summary>Returns the latest authoritative status without KYC documents or provider data.</summary>
    [HttpGet("status/summary")]
    public async Task<ActionResult<AZOAResult<KycStatusSummaryResponse>>> GetStatusSummary(CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();

        if ((IsApiKeyPrincipal() || User.GetActingTenantId() is not null)
            && !User.HasScope(AzoaScopes.KycRead))
            return StatusCode(
                StatusCodes.Status403Forbidden,
                AZOAResult<KycStatusSummaryResponse>.FailureWithCode(
                    $"Caller lacks the '{AzoaScopes.KycRead}' scope.",
                    AzoaErrorCodes.Forbidden));

        var result = await _manager.GetStatusAsync(avatarId.Value, ct);
        if (result.IsError)
        {
            if (result.Message?.StartsWith(KycAuthorizationError.NotFound, StringComparison.Ordinal) == true)
                return Ok(AZOAResult<KycStatusSummaryResponse>.Success(new KycStatusSummaryResponse()));
            return BadRequest(AZOAResult<KycStatusSummaryResponse>.Failure(
                result.Message ?? "KYC status could not be loaded."));
        }

        var submission = result.Result!;
        return Ok(AZOAResult<KycStatusSummaryResponse>.Success(new KycStatusSummaryResponse
        {
            HasSubmission = true,
            IsVerified = submission.Status == KycStatus.APPROVED,
            Status = submission.Status.ToString(),
            SubmittedAt = submission.SubmittedAt,
            UpdatedAt = submission.ModifiedDate ?? submission.SubmittedAt,
            ExpiresAt = submission.ExpiresAt
        }));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "FirstPartyLogin")]
    public async Task<ActionResult<AZOAResult<KycSubmissionModel>>> GetById(Guid id, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();
        if (User.GetActingTenantId() is not null) return Forbid();

        var result = await _manager.GetByIdAsync(id, avatarId.Value, ct);
        return TranslateResult(result);
    }

    [HttpGet("{id:guid}/documents")]
    [Authorize(Policy = "FirstPartyLogin")]
    public async Task<ActionResult<AZOAResult<IEnumerable<KycDocumentModel>>>> GetDocuments(Guid id, CancellationToken ct)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null) return Unauthorized();
        if (User.GetActingTenantId() is not null) return Forbid();

        var result = await _manager.ListDocumentsAsync(id, avatarId.Value, ct);
        return TranslateResult(result);
    }

    // ── Admin surface (gated behind the Operator policy, see class remarks) ────

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

    private bool IsApiKeyPrincipal()
        => string.Equals(
            User.FindFirst("AuthMethod")?.Value,
            "ApiKey",
            StringComparison.OrdinalIgnoreCase);
}
