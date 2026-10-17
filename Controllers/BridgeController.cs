using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// Cross-chain bridge endpoints. Launch initiation is limited to providers
/// that advertise the complete trusted custody lifecycle; Wormhole is blocked.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class BridgeController : ControllerBase
{
    private readonly ICrossChainBridgeService _bridgeService;

    public BridgeController(ICrossChainBridgeService bridgeService)
    {
        _bridgeService = bridgeService;
    }

    /// <summary>
    /// Get all supported bridge routes between chains.
    /// </summary>
    [HttpGet("routes")]
    [ProducesResponseType(typeof(IEnumerable<BridgeRouteInfo>), 200)]
    public async Task<IActionResult> GetRoutes(CancellationToken ct)
    {
        var result = await _bridgeService.GetSupportedRoutesAsync(ct);
        if (result.IsError)
            return BadRequest(result.ToErrorPayload());

        return Ok(result.Result);
    }

    /// <summary>
    /// Initiate a trusted bridge on a complete, server-controlled route.
    /// </summary>
    [HttpPost("initiate")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(BridgeTransactionResult), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> InitiateBridge(
        [FromBody] BridgeInitiateRequest request,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();
        if (SigningRefusal() is { } refused) return refused;

        // Amount is a precision-safe string on the wire and is parsed once into
        // the provider-safe unsigned base-unit range.
        if (!ulong.TryParse(request.Amount, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount == 0)
            return BadRequest(new { error = "Amount must be a positive unsigned 64-bit integer." });

        // Optional client Idempotency-Key — user-namespaced by the service so a
        // retried initiate collapses to one chain effect without cross-user collisions.
        // Absent ⇒ null ⇒ service uses its deterministic content key (safe).
        var idempotencyKey = ReadIdempotencyKey();

        var result = await _bridgeService.InitiateBridgeAsync(
            request.SourceChain, request.TargetChain, request.TokenId,
            request.RecipientAddress, userId, amount,
            request.Mode, ct, idempotencyKey);

        if (result.IsError)
            return BadRequest(result.ToErrorPayload());

        return Ok(result.Result);
    }

    /// <summary>
    /// Fetch the signed VAA from the Wormhole Guardian network.
    /// Call this after initiating a Wormhole bridge to poll for Guardian consensus.
    /// </summary>
    [HttpPost("{id}/fetch-vaa")]
    [ProducesResponseType(typeof(BridgeTransactionResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> FetchVAA(string id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();
        var result = await _bridgeService.FetchVAAAsync(id, ct, userId);
        if (result.IsError)
        {
            if (result.Message.Contains("not found"))
                return NotFound(result.ToErrorPayload());
            return BadRequest(result.ToErrorPayload());
        }

        return Ok(result.Result);
    }

    /// <summary>
    /// Redeem a Wormhole bridge on the target chain.
    /// Submits the verified VAA to the target chain's Token Bridge to complete
    /// the trustless transfer. The VAA must have been fetched first.
    /// </summary>
    [HttpPost("{id}/redeem")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(BridgeTransactionResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> RedeemWithVAA(string id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();
        if (SigningRefusal() is { } refused) return refused;
        var idempotencyKey = ReadIdempotencyKey();
        var result = await _bridgeService.RedeemWithVAAAsync(id, ct, idempotencyKey, userId);
        if (result.IsError)
        {
            if (result.Message.Contains("not found"))
                return NotFound(result.ToErrorPayload());
            return BadRequest(result.ToErrorPayload());
        }

        return Ok(result.Result);
    }

    /// <summary>
    /// Reverse a completed bridge: burn wrapped, release original.
    /// </summary>
    [HttpPost("{id}/reverse")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(BridgeTransactionResult), 200)]
    public async Task<IActionResult> ReverseBridge(
        string id,
        [FromBody] BridgeReverseRequest request,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();
        if (SigningRefusal() is { } refused) return refused;
        var idempotencyKey = ReadIdempotencyKey();
        var result = await _bridgeService.ReverseBridgeAsync(id, request.SourceRecipientAddress, ct, idempotencyKey, userId);
        if (result.IsError)
            return BadRequest(result.ToErrorPayload());

        return Ok(result.Result);
    }

    /// <summary>
    /// Get status of a specific bridge transaction.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(BridgeTransactionResult), 200)]
    public async Task<IActionResult> GetBridgeStatus(string id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();
        var result = await _bridgeService.GetBridgeStatusAsync(id, ct, userId);
        if (result.IsError)
            return NotFound(result.ToErrorPayload());

        return Ok(result.Result);
    }

    /// <summary>
    /// Get bridge history for the authenticated user.
    /// </summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(IEnumerable<BridgeTransactionResult>), 200)]
    public async Task<IActionResult> GetHistory(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();
        var result = await _bridgeService.GetBridgeHistoryAsync(userId, ct);
        if (result.IsError)
            return BadRequest(result.ToErrorPayload());

        return Ok(result.Result);
    }

    /// <summary>
    /// Reads the optional client <c>Idempotency-Key</c> request header.
    /// Returns null when absent/blank so the bridge service falls back to its
    /// deterministic content-addressed key (never a random per-request key —
    /// absence must stay dedup-safe).
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

    /// <summary>Audit-2 M8: value-moving bridge routes need the transfer signing scope and never run under a tenant-delegated credential.</summary>
    private IActionResult? SigningRefusal()
    {
        if (User.GetActingTenantId() is not null)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Tenant-delegated credentials cannot drive the legacy bridge." });
        return User.HasSigningScope(AzoaScopes.TransferSign) ? null
            : StatusCode(StatusCodes.Status403Forbidden, new { error = $"The '{AzoaScopes.TransferSign}' signing scope is required." });
    }

    private bool TryGetUserId(out Guid userId)
    {
        var userClaim = User.FindFirst("userId")?.Value;
        if (!string.IsNullOrWhiteSpace(userClaim))
            return Guid.TryParse(userClaim, out userId) && userId != Guid.Empty;

        return AzoaClaims.TryGetSubjectId(User, out userId) && userId != Guid.Empty;
    }
}

// ─── Request DTOs ───

public class BridgeInitiateRequest
{
    public string SourceChain { get; set; } = string.Empty;
    public string TargetChain { get; set; } = string.Empty;
    public string TokenId { get; set; } = string.Empty;
    public string RecipientAddress { get; set; } = string.Empty;

    /// <summary>
    /// Bridge amount in base units as a decimal string in the provider-safe
    /// unsigned 64-bit range.
    /// </summary>
    public string Amount { get; set; } = "1";

    /// <summary>
    /// Bridge mode: null = server default, "Trusted" = custodial, "Wormhole" = trustless.
    /// </summary>
    public BridgeMode? Mode { get; set; }
}

public class BridgeReverseRequest
{
    public string SourceRecipientAddress { get; set; } = string.Empty;
}
