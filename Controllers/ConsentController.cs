using Microsoft.AspNetCore.Authorization;
using AZOA.WebAPI.Services.Consent;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

/// <summary>Authenticated owner consent commands and scoped grant reads; see Controllers/AGENTS.md.</summary>
[ApiController]
[Route("api/user/consent")]
[Authorize(Policy = "FirstPartyLogin")]
public class ConsentController : ControllerBase
{
    private readonly IConsentManager _manager;

    private readonly ConsentLifecycleService _lifecycle;

    public ConsentController(IConsentManager manager, ConsentLifecycleService lifecycle)
    {
        _manager = manager;
        _lifecycle = lifecycle;
    }

    /// <summary>Admits an immutable caller-identified grant command using actual owner authority.</summary>
    [HttpPost]
    public async Task<ActionResult<AZOAResult<ConsentLifecycleReceipt>>> Grant([FromBody] GrantConsentRequest request)
    {
        if (request is null || request.RequestId == Guid.Empty || !TryParseOrigin(request.Origin, out var origin))
            return BadRequest(AZOAResult<ConsentLifecycleReceipt>.Failure("A persisted requestId and valid origin are required."));
        if (request.ExpiresAt is { Kind: DateTimeKind.Unspecified })
            return BadRequest(AZOAResult<ConsentLifecycleReceipt>.Failure("expiresAt must include a timezone."));
        var command = new ConsentLifecycleCommand(request.RequestId, request.TenantId, request.Scopes,
            origin, request.ParticipationRef, request.ExpiresAt is { } expiry ? new DateTimeOffset(expiry).ToUniversalTime() : null);
        var result = await _lifecycle.GrantAsync(User, command, HttpContext.RequestAborted);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    /// <summary>Withdraws an exact pending command or returns its original admitted grant.</summary>
    [HttpPost("withdraw")]
    public async Task<ActionResult<AZOAResult<ConsentCommandWithdrawal>>> Withdraw([FromBody] GrantConsentRequest request)
    {
        if (request is null || request.RequestId == Guid.Empty || !TryParseOrigin(request.Origin, out var origin)
            || request.ExpiresAt is { Kind: DateTimeKind.Unspecified })
            return BadRequest(AZOAResult<ConsentCommandWithdrawal>.Failure("An exact saved consent request is required."));
        var command = new ConsentLifecycleCommand(request.RequestId, request.TenantId, request.Scopes,
            origin, request.ParticipationRef, request.ExpiresAt is { } expiry ? new DateTimeOffset(expiry).ToUniversalTime() : null);
        var result = await _lifecycle.WithdrawAsync(User, command, HttpContext.RequestAborted);
        return result.IsError ? Conflict(result) : Ok(result);
    }

    /// <summary>Atomically revokes an owned grant and returns its original revocation receipt.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<AZOAResult<ConsentRevocationReceipt>>> Revoke(Guid id)
    {
        var result = await _lifecycle.RevokeAsync(User, id, HttpContext.RequestAborted);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    /// <summary>Lists the authenticated owner's grants.</summary>
    [HttpGet]
    public async Task<ActionResult<AZOAResult<IEnumerable<ConsentGrantResponse>>>> List()
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized();

        var result = await _manager.ListForUserAsync(userId.Value, HttpContext.RequestAborted);
        if (result.IsError) return BadRequest(result);

        return Ok(ToResponseList(result));
    }

    /// <summary>Reads one grant owned by the authenticated user.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AZOAResult<ConsentGrantResponse>>> Get(Guid id)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null) return Unauthorized();

        var result = await _manager.GetForUserAsync(userId.Value, id, HttpContext.RequestAborted);
        return TranslateGrant(result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Projects a scoped grant read into its public response.</summary>
    private ActionResult<AZOAResult<ConsentGrantResponse>> TranslateGrant(AZOAResult<ConsentGrant> result)
    {
        if (result.IsError || result.Result is null)
        {
            var mapped = new AZOAResult<ConsentGrantResponse> { IsError = true, Message = result.Message ?? "Grant not found.", Exception = result.Exception };
            return IsNotFound(result.Message) || result.Result is null ? NotFound(mapped) : BadRequest(mapped);
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

    /// <summary>Parses the optional grant origin.</summary>
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

    /// <summary>Reads the authenticated subject identifier.</summary>
    private Guid? GetUserIdFromClaims()
    {
        var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User?.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
