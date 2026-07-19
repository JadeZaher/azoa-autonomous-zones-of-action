// SPDX-License-Identifier: UNLICENSED

using System.Security.Claims;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// Caller-scoped allocation receipt and observation-only reconciliation routes.
/// See <c>Controllers/AGENTS.md</c>.
/// </summary>
[ApiController]
[Route("api/allocation/receipt")]
[Authorize]
public sealed class AllocationReceiptController : ControllerBase
{
    private readonly IAllocationReceiptManager _receiptManager;

    public AllocationReceiptController(IAllocationReceiptManager receiptManager)
    {
        _receiptManager = receiptManager ?? throw new ArgumentNullException(nameof(receiptManager));
    }

    /// <summary>Returns the receipt bound to this API key and <c>Idempotency-Key</c>.</summary>
    [HttpGet]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<AllocationReceiptResponse>>> Get(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var requestResult = CreateRequest(idempotencyKey);
        if (requestResult.Result is null)
            return requestResult.Error!;

        return Translate(await _receiptManager.GetAsync(requestResult.Result, ct));
    }

    /// <summary>Observes chain truth for this receipt without resubmitting its allocation.</summary>
    [HttpPost("reconcile")]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<AllocationReceiptResponse>>> Reconcile(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var requestResult = CreateRequest(idempotencyKey);
        if (requestResult.Result is null)
            return requestResult.Error!;

        return Translate(await _receiptManager.ReconcileAsync(requestResult.Result, ct));
    }

    private (AllocationReceiptRequest? Result, ActionResult<AZOAResult<AllocationReceiptResponse>>? Error) CreateRequest(
        string? idempotencyKey)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return (null, Unauthorized(new AZOAResult<AllocationReceiptResponse>
            {
                IsError = true,
                Message = "Invalid token.",
            }));
        }

        if (!string.Equals(User.FindFirst("AuthMethod")?.Value, "ApiKey", StringComparison.OrdinalIgnoreCase))
        {
            return (null, StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<AllocationReceiptResponse>.FailureWithCode(
                    "Allocation receipts require an API-key principal.",
                    AzoaErrorCodes.Forbidden)));
        }

        if (!User.HasScope(AzoaScopes.NftMint))
        {
            return (null, StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<AllocationReceiptResponse>.FailureWithCode(
                    $"Caller lacks the '{AzoaScopes.NftMint}' scope required to read an allocation receipt.",
                    AzoaErrorCodes.Forbidden)));
        }

        if (!Guid.TryParse(User.FindFirst("ApiKeyId")?.Value, out var apiKeyId) || apiKeyId == Guid.Empty ||
            !TryGetAvatarId(out var avatarId))
        {
            return (null, Unauthorized(new AZOAResult<AllocationReceiptResponse>
            {
                IsError = true,
                Message = "Invalid API-key identity.",
            }));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return (null, BadRequest(AZOAResult<AllocationReceiptResponse>.FailureWithCode(
                "Idempotency-Key is required.",
                AzoaErrorCodes.InvalidRequest)));
        }

        return (new AllocationReceiptRequest
        {
            ApiKeyId = apiKeyId,
            CallerAvatarId = avatarId,
            ClientIdempotencyKey = idempotencyKey.Trim(),
        }, null);
    }

    private ActionResult<AZOAResult<AllocationReceiptResponse>> Translate(
        AZOAResult<AllocationReceiptResponse> result)
    {
        if (!result.IsError)
            return Ok(result);

        if (string.Equals(result.Code, AzoaErrorCodes.DependencyUnavailable, StringComparison.Ordinal))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, result);

        return string.Equals(result.Code, AzoaErrorCodes.NotFound, StringComparison.Ordinal)
            ? NotFound(result)
            : BadRequest(result);
    }

    private bool TryGetAvatarId(out Guid avatarId)
    {
        var value = User.FindFirst("AvatarId")?.Value
                    ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(value, out avatarId) && avatarId != Guid.Empty;
    }
}
