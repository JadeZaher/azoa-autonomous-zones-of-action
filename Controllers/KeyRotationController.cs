// SPDX-License-Identifier: UNLICENSED

using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Interfaces.Managers;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// Operator surface for live wrapping-key rotation (final-hardening B5). Batch
/// re-wraps every stored wallet from the current <c>AZOA:WalletEncryptionKey</c> to
/// a new one. Guarded by the hardened <c>Operator</c> policy (security-review HIGH-2:
/// JWT-scheme floor + explicit operator capability — an API key can NEVER reach this)
/// and rate-limited by the <c>financial</c> policy, since a rotation rewrites key
/// material across every avatar.
/// </summary>
/// <remarks>Contract + rationale: <c>Services/Custody/AGENTS.md</c> §rotation.</remarks>
[ApiController]
[Route("api/admin/key-rotation")]
[Authorize(Policy = "Operator")]
public sealed class KeyRotationController : ControllerBase
{
    private readonly IKeyRotationService _rotation;

    public KeyRotationController(IKeyRotationService rotation)
    {
        _rotation = rotation;
    }

    /// <summary>Request body for a rotation: the new wrapping key to re-wrap under.</summary>
    public sealed class RotateRequest
    {
        /// <summary>The new <c>AZOA:WalletEncryptionKey</c> secret to re-wrap every wallet under.</summary>
        public string NewEncryptionKey { get; set; } = string.Empty;
    }

    /// <summary>
    /// Re-wraps every wallet from the current data-key to
    /// <see cref="RotateRequest.NewEncryptionKey"/>. Idempotent/resumable (already
    /// rotated wallets are skipped) and all-or-nothing (a partial failure rolls the
    /// batch back to its pre-rotation state). Returns counts only — never key material.
    /// </summary>
    [HttpPost("rotate")]
    [EnableRateLimiting("financial")] // security-review HIGH-2: throttle the most destructive endpoint in the tree.
    [ProducesResponseType(typeof(KeyRotationReport), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> Rotate([FromBody] RotateRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.NewEncryptionKey))
            return BadRequest(new { error = "newEncryptionKey is required." });

        var result = await _rotation.RotateAllAsync(request.NewEncryptionKey, ct);
        if (result.IsError)
            return BadRequest(new { error = result.Message, report = result.Result });

        return Ok(result.Result);
    }
}
