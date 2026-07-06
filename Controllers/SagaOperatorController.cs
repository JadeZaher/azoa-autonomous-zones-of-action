// SPDX-License-Identifier: UNLICENSED

using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Sagas;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// Operator/dead-letter surface for the durable saga outbox (Phase-F). The
/// GENERIC saga-step primitive beneath the quest-specific reconcile board
/// (<c>QuestController</c> runs/reconcile-sweep, B4): list parked/failed/
/// dead-lettered steps, requeue a revivable one, or terminally cancel one.
/// Guarded by the hardened <c>Operator</c> policy (JWT-scheme floor + explicit
/// operator capability — an API key can NEVER reach this), the same gate as the
/// cross-avatar reconcile sweep, since these ops mutate the durable outbox
/// across every saga instance and avatar.
/// </summary>
/// <remarks>Contract + relationship to the B4 quest reconcile sweep:
/// <c>Services/Sagas/AGENTS.md §operator-surface</c>.</remarks>
[ApiController]
[Route("api/admin/sagas")]
[Authorize(Policy = "Operator")]
public sealed class SagaOperatorController : ControllerBase
{
    private const int DefaultListLimit = 100;

    private readonly ISagaStore _store;

    public SagaOperatorController(ISagaStore store)
    {
        _store = store;
    }

    /// <summary>
    /// List steps parked in the dead-letter queue (default), or any explicit
    /// subset of {DeadLettered, Parked, Cancelled}, newest-updated first. The
    /// operator's diagnosis surface.
    /// </summary>
    [HttpGet("dead-letters")]
    [ProducesResponseType(typeof(IReadOnlyList<SagaStepView>), 200)]
    public async Task<IActionResult> ListDeadLetters(
        [FromQuery] string[]? status,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var statuses = ParseStatuses(status);
        if (statuses is null)
            return BadRequest(new { message = "Unrecognised status filter. Allowed: DeadLettered, Parked, Cancelled." });

        var steps = await _store.ListByStatusesAsync(statuses, limit ?? DefaultListLimit, ct);
        return Ok(steps.Select(SagaStepView.From).ToList());
    }

    /// <summary>
    /// Requeue a specific parked/dead-lettered step (reset to Pending so the
    /// processor picks it up again). Idempotent; refuses to revive a cancelled or
    /// completed step (404 when no revivable row matched).
    /// </summary>
    [HttpPost("{id:guid}/requeue")]
    [EnableRateLimiting("financial")] // mutating admin op — throttled like the backfill apply.
    public async Task<IActionResult> Requeue(Guid id, CancellationToken ct)
    {
        var applied = await _store.RequeueStepAsync(id, ct);
        if (!applied)
            return NotFound(new { message = $"No revivable (Parked/DeadLettered) saga step '{id}' — already running, completed, or cancelled." });
        return Ok(new { id, status = "Pending", message = "Requeued; the processor will claim it on the next tick." });
    }

    /// <summary>
    /// Terminally cancel a specific parked/failed/pending step so it never
    /// retries. Idempotent; will not un-complete a completed step or yank an
    /// in-progress (leased) one (404 when no cancellable row matched).
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [EnableRateLimiting("financial")]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] SagaStepCancelRequest? body, CancellationToken ct)
    {
        var reason = string.IsNullOrWhiteSpace(body?.Reason)
            ? "Cancelled by operator."
            : body!.Reason!;
        var applied = await _store.CancelStepAsync(id, reason, ct);
        if (!applied)
            return NotFound(new { message = $"No cancellable (Pending/Parked/DeadLettered) saga step '{id}' — already completed, in-progress, or cancelled." });
        return Ok(new { id, status = "Cancelled", message = "Step terminally cancelled; it will not retry." });
    }

    /// <summary>Maps the caller's status filter to the domain enum; null on any bad token.</summary>
    private static IReadOnlyCollection<StepStatus>? ParseStatuses(string[]? raw)
    {
        if (raw is null || raw.Length == 0)
            return new[] { StepStatus.DeadLettered };

        var result = new List<StepStatus>(raw.Length);
        foreach (var s in raw)
        {
            if (!Enum.TryParse<StepStatus>(s, ignoreCase: true, out var parsed))
                return null;
            // Only the operator-inspectable at-rest states are listable here.
            if (parsed is not (StepStatus.DeadLettered or StepStatus.Parked or StepStatus.Cancelled))
                return null;
            result.Add(parsed);
        }
        return result;
    }
}

/// <summary>Operator-facing projection of a saga step (diagnosis fields only — no opaque payload).</summary>
public sealed record SagaStepView(
    Guid Id,
    string SagaName,
    string StepName,
    string CorrelationKey,
    string Status,
    bool IsCompensation,
    int AttemptCount,
    string? LastError,
    string? GateId,
    DateTime NextRunAt,
    DateTime UpdatedAt)
{
    public static SagaStepView From(AZOA.WebAPI.Models.Sagas.SagaStepRecord r) => new(
        r.Id, r.SagaName, r.StepName, r.CorrelationKey, r.Status.ToString(),
        r.IsCompensation, r.AttemptCount, r.LastError, r.GateId, r.NextRunAt, r.UpdatedAt);
}

/// <summary>Optional operator note recorded on a cancel (audit trail).</summary>
public sealed class SagaStepCancelRequest
{
    public string? Reason { get; set; }
}
