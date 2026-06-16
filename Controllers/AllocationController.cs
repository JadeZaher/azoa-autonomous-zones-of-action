// SPDX-License-Identifier: UNLICENSED

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Controllers;

/// <summary>
/// The tenant-callable wallet-provision + asset-allocation seam. A
/// fiat-settlement tenant authenticates with its <c>X-Api-Key</c> (admitted by
/// the JWT-or-ApiKey multi-scheme policy via <c>[Authorize]</c>) and calls this
/// AFTER money has cleared on its own platform.
///
/// This controller holds NO payment-provider secret, runs NO checkout, and
/// exposes NO webhook handler — it trusts the tenant via the API key, not via any
/// payment-provider signature. Token economics stay in the tenant; OASIS receives
/// an already-decided amount.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AllocationController : ControllerBase
{
    private readonly IAllocationManager _allocationManager;

    public AllocationController(IAllocationManager allocationManager)
    {
        _allocationManager = allocationManager;
    }

    /// <summary>
    /// Provision-if-absent + allocate an already-decided amount of an asset into
    /// the target avatar's custodial wallet. Idempotent on the
    /// <c>Idempotency-Key</c> header (partitioned by the caller's API key), so a
    /// redelivered fiat webhook never double-mints / double-transfers.
    ///
    /// IDOR: the target is the <paramref name="avatarId"/> route value; no body
    /// field can redirect the allocation to a different avatar.
    /// </summary>
    [HttpPost("{avatarId:guid}")]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<OASISResult<AllocationResult>>> Allocate(
        Guid avatarId, [FromBody] AllocationRequest request)
    {
        var callerAvatarId = GetAvatarIdFromClaims();
        if (callerAvatarId is null)
            return Unauthorized(new OASISResult<AllocationResult> { IsError = true, Message = "Invalid token." });

        // The allocation is authorised by the API-key scope, not the body.
        if (!User.HasScope(OasisScopes.NftMint) && !User.HasScope(OasisScopes.WalletManage))
            return StatusCode(StatusCodes.Status403Forbidden, new OASISResult<AllocationResult>
            {
                IsError = true,
                Message = $"Caller lacks the '{OasisScopes.NftMint}' scope required to allocate."
            });

        var apiKeyId = GetApiKeyId();
        if (string.IsNullOrEmpty(apiKeyId))
            return Unauthorized(new OASISResult<AllocationResult>
            {
                IsError = true,
                Message = "Allocation requires an API-key principal (missing ApiKeyId claim)."
            });

        // Client Idempotency-Key wins; blank ⇒ null ⇒ the manager derives a
        // deterministic content key (never a random per-request key).
        var idempotencyKey = ReadIdempotencyKey();

        var result = await _allocationManager.AllocateAsync(
            avatarId, request, callerAvatarId.Value, idempotencyKey, apiKeyId);

        if (!result.IsError) return Ok(result);

        // Fail-closed KYC surfaces a KYC_FORBIDDEN: prefix → 403.
        if (result.Message?.StartsWith(KycAuthorizationError.Forbidden, StringComparison.Ordinal) == true)
            return StatusCode(StatusCodes.Status403Forbidden, result);

        return BadRequest(result);
    }

    /// <summary>
    /// Reads the optional client <c>Idempotency-Key</c> header (mirrors
    /// <c>WalletController.ReadIdempotencyKey</c>). Returns null when absent/blank
    /// so the manager falls back to its deterministic content-addressed key.
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

    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private string? GetApiKeyId()
    {
        var value = User.FindFirst("ApiKeyId")?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
