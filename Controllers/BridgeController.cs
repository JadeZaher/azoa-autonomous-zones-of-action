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
        if (!TryGetAvatarId(out var avatarId))
            return Unauthorized();

        // Amount is a precision-safe string on the wire and is parsed once into
        // the provider-safe unsigned base-unit range.
        if (!ulong.TryParse(request.Amount, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount == 0)
            return BadRequest(new { error = "Amount must be a positive unsigned 64-bit integer." });

        // Optional client Idempotency-Key — avatar-namespaced by the service so a
        // retried initiate collapses to one chain effect without cross-avatar collisions.
        // Absent ⇒ null ⇒ service uses its deterministic content key (safe).
        var idempotencyKey = ReadIdempotencyKey();

        var result = await _bridgeService.InitiateBridgeAsync(
            request.SourceChain, request.TargetChain, request.TokenId,
            request.RecipientAddress, avatarId, amount,
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
        if (!TryGetAvatarId(out var avatarId))
            return Unauthorized();
        var result = await _bridgeService.FetchVAAAsync(id, ct, avatarId);
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
        if (!TryGetAvatarId(out var avatarId))
            return Unauthorized();
        var idempotencyKey = ReadIdempotencyKey();
        var result = await _bridgeService.RedeemWithVAAAsync(id, ct, idempotencyKey, avatarId);
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
        if (!TryGetAvatarId(out var avatarId))
            return Unauthorized();
        var idempotencyKey = ReadIdempotencyKey();
        var result = await _bridgeService.ReverseBridgeAsync(id, request.SourceRecipientAddress, ct, idempotencyKey, avatarId);
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
        if (!TryGetAvatarId(out var avatarId))
            return Unauthorized();
        var result = await _bridgeService.GetBridgeStatusAsync(id, ct, avatarId);
        if (result.IsError)
            return NotFound(result.ToErrorPayload());

        return Ok(result.Result);
    }

    /// <summary>
    /// Get bridge history for the authenticated avatar.
    /// </summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(IEnumerable<BridgeTransactionResult>), 200)]
    public async Task<IActionResult> GetHistory(CancellationToken ct)
    {
        if (!TryGetAvatarId(out var avatarId))
            return Unauthorized();
        var result = await _bridgeService.GetBridgeHistoryAsync(avatarId, ct);
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

    private bool TryGetAvatarId(out Guid avatarId)
    {
        var avatarClaim = User.FindFirst("avatarId")?.Value;
        if (!string.IsNullOrWhiteSpace(avatarClaim))
            return Guid.TryParse(avatarClaim, out avatarId) && avatarId != Guid.Empty;

        return AzoaClaims.TryGetSubjectId(User, out avatarId) && avatarId != Guid.Empty;
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
