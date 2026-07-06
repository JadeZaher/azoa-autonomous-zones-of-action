// SPDX-License-Identifier: UNLICENSED

using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Services.Backfill;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// Operator surface for the data-backfill primitive: list registered backfills
/// with applied/pending status and apply the pending ones. Guarded by the hardened
/// <c>Operator</c> policy (security-review HIGH-2: JWT-scheme floor + explicit
/// operator capability — an API key can NEVER reach this) — the same gate as the
/// cross-avatar reconcile sweep, since a backfill rewrites data across avatars.
/// The apply endpoint is additionally rate-limited by the <c>financial</c> policy.
/// </summary>
/// <remarks>Contract + rationale: <c>Services/Backfill/AGENTS.md</c>.</remarks>
[ApiController]
[Route("api/admin/backfill")]
[Authorize(Policy = "Operator")]
public sealed class BackfillController : ControllerBase
{
    private readonly BackfillRunner _runner;

    public BackfillController(BackfillRunner runner)
    {
        _runner = runner;
    }

    /// <summary>Lists every registered backfill with its applied/pending status, in apply order.</summary>
    [HttpGet("list")]
    [ProducesResponseType(typeof(IReadOnlyList<BackfillStatus>), 200)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await _runner.ListAsync(ct));

    /// <summary>Applies every not-yet-applied backfill in order and records each. Idempotent — re-running is a no-op.</summary>
    [HttpPost("apply")]
    [EnableRateLimiting("financial")] // security-review HIGH-2: throttle the cross-avatar mutating admin endpoint.
    [ProducesResponseType(typeof(IReadOnlyList<BackfillApplyOutcome>), 200)]
    public async Task<IActionResult> Apply(CancellationToken ct)
        => Ok(await _runner.ApplyPendingAsync(ct));
}
