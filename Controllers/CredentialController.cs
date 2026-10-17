// SPDX-License-Identifier: UNLICENSED

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

/// <summary>Authenticated credential issuance and revocation.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CredentialController : ControllerBase
{
    private readonly ICredentialManager _credentialManager;
    private readonly INftLifecycleService _lifecycle;
    private readonly AZOA.WebAPI.Services.Signing.SigningAdmissionAuthorityFactory? _signingAuthority;

    public CredentialController(ICredentialManager credentialManager, INftLifecycleService lifecycle,
        AZOA.WebAPI.Services.Signing.SigningAdmissionAuthorityFactory? signingAuthority = null)
    {
        _credentialManager = credentialManager;
        _lifecycle = lifecycle;
        _signingAuthority = signingAuthority;
    }

    /// <summary>Quotes the exact issuer-funded credential creation before participant consent.</summary>
    [HttpPost("issue/{recipientUserId:guid}/cost-quote")]
    [EnableRateLimiting("financial")]
    public async Task<IActionResult> QuoteIssueCosts(Guid recipientUserId, [FromBody] UserNFTMintModel request)
    {
        var caller = GetUserIdFromClaims();
        if (caller is null) return Unauthorized();
        if (!User.HasDappManagerRole() || !User.HasSigningScope(AzoaScopes.NftMint)) return Forbid();
        if (caller.Value == recipientUserId)
            return BadRequest(AZOAResult<NftProtocolCostOffer>.Failure("A credential requires a distinct issuer and recipient."));
        var model = request ?? new UserNFTMintModel();
        model.IsSoulbound = true;
        model.IsTransferable = false;
        var result = await _lifecycle.QuoteMintCostsAsync(caller.Value, recipientUserId, model,
            ReadIdempotencyKey() ?? string.Empty, HttpContext.RequestAborted, User.GetActingTenantId() ?? Guid.Empty);
        return result.IsError || result.Result is null ? BadRequest(result)
            : Ok(AZOAResult<NftProtocolCostOffer>.Success(NftProtocolCostOffer.From(result.Result)));
    }

    /// <summary>Starts a durable credential issuance that requires recipient acceptance and chain confirmation.</summary>
    [HttpPost("issue/{recipientUserId:guid}")]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<NftLifecycleView>>> Issue(
        Guid recipientUserId, [FromBody] UserNFTMintModel request)
    {
        var callerUserId = GetUserIdFromClaims();
        if (callerUserId is null)
            return Unauthorized(AZOAResult<NftLifecycleView>.Failure("Invalid token."));

        if (!User.HasDappManagerRole())
            return StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<NftLifecycleView>.FailureWithCode(
                    $"Issuing a credential to another user requires the '{AzoaDappRoles.Manager}' role. " +
                    "A credential is soulbound and revocable only by its issuer, so an unsolicited one " +
                    "cannot be removed by the person carrying it.",
                    AzoaErrorCodes.Forbidden));

        if (!User.HasSigningScope(AzoaScopes.NftMint))
            return StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<NftLifecycleView>.FailureWithCode(
                    $"Caller lacks the '{AzoaScopes.NftMint}' scope required to issue a credential.",
                    AzoaErrorCodes.Forbidden));

        var model = request ?? new UserNFTMintModel();

        model.IsSoulbound = true;
        model.IsTransferable = false;

        if (callerUserId.Value == recipientUserId)
            return BadRequest(AZOAResult<NftLifecycleView>.Failure("A credential requires a distinct issuer and recipient."));
        var captured = await (_signingAuthority ?? throw new InvalidOperationException("Signing authority capture is not registered."))
            .CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        var result = await _lifecycle.MintAsync(callerUserId.Value, recipientUserId, model, ReadIdempotencyKey() ?? string.Empty,
            HttpContext.RequestAborted, User.GetActingTenantId() ?? Guid.Empty, credential: captured.Result);
        return result.IsError ? BadRequest(result) : result.Result?.State == "Completed" ? Ok(result) : Accepted(result);
    }

    /// <summary>Revokes a credential issued by this caller; pending confirmation returns 202.</summary>
    /// <response code="200">The clawback confirmed on chain; the credential is revoked.</response>
    /// <response code="202">Broadcast but NOT confirmed — outcome unknown, do not re-submit.</response>
    /// <response code="403">Caller lacks the revoke scope, is not the recorded issuer, or fails the KYC gate.</response>
    [HttpPost("{assetId}/revoke")]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<CredentialRevokeResult>>> Revoke(
        string assetId, [FromBody] CredentialRevokeRequest request)
    {
        var callerUserId = GetUserIdFromClaims();
        if (callerUserId is null)
            return Unauthorized(AZOAResult<CredentialRevokeResult>.Failure("Invalid token."));

        if (!User.HasSigningScope(AzoaScopes.CredentialRevokeSign))
            return StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<CredentialRevokeResult>.FailureWithCode(
                    $"Caller lacks the '{AzoaScopes.CredentialRevokeSign}' scope required to revoke a credential.",
                    AzoaErrorCodes.Forbidden));

        var captured = await (_signingAuthority ?? throw new InvalidOperationException("Signing authority capture is not registered."))
            .CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        request ??= new CredentialRevokeRequest();
        request.AuthenticationWitness = captured.Result;
        var actingTenantId = User.GetActingTenantId();

        var result = await _credentialManager.RevokeAsync(
            assetId,
            request ?? new CredentialRevokeRequest(),
            callerUserId.Value,
            ReadIdempotencyKey(),
            actingTenantId);

        if (!result.IsError) return Ok(result);

        if (string.Equals(result.Code, CredentialRevokeCodes.PendingConfirmation, StringComparison.Ordinal))
            return Accepted(AZOAResult<CredentialRevokeResult>.Success(result.Result!, result.Message));

        if (string.Equals(result.Code, AzoaErrorCodes.Forbidden, StringComparison.Ordinal))
            return StatusCode(StatusCodes.Status403Forbidden, result);

        if (string.Equals(result.Code, AzoaErrorCodes.Conflict, StringComparison.Ordinal))
            return Conflict(result);

        if (result.Message?.StartsWith(KycAuthorizationError.Forbidden, StringComparison.Ordinal) == true)
            return StatusCode(StatusCodes.Status403Forbidden, result);

        return BadRequest(result);
    }

    /// <summary>Reads the caller-provided durable request key.</summary>
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

    private Guid? GetUserIdFromClaims()
    {
        var sub = User.FindFirst("UserId")?.Value
                 ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
