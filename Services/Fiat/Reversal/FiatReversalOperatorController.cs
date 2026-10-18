// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Sagas;
using SurrealForge.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Services.NodeAdmin;

namespace AZOA.WebAPI.Services.Fiat.Reversal;

/// <summary>
/// The operator's view of money that has been taken back, and the three things a person can do
/// about one: run the unwind now, close it, or lift the pause on an account.
///
/// <para>Guarded by the hardened <c>Operator</c> policy (JWT floor plus an explicit operator
/// capability — an API key can never reach it), the same gate the saga dead-letter board and the
/// platform-unit surface use. Every verb here moves money or lifts a control.</para>
///
/// <para>Colocated with the pipeline rather than under <c>Controllers/</c> because that directory
/// belongs to other items in this wave; MVC discovers controllers by type, not by folder, so the
/// route is identical either way and moving the file later changes nothing.</para>
/// </summary>
[ApiController]
[Route("api/admin/fiat-reversals")]
[Authorize(Policy = "Operator")]
public sealed class FiatReversalOperatorController : ControllerBase
{
    private const int DefaultListLimit = 100;

    private readonly IFiatReversalLedger _reversals;
    private readonly IReversalCompensation _compensation;
    private readonly ISagaCoordinator _sagas;
    private readonly ILogger<FiatReversalOperatorController> _logger;

    public FiatReversalOperatorController(
        IFiatReversalLedger reversals,
        IReversalCompensation compensation,
        ISagaCoordinator sagas,
        ILogger<FiatReversalOperatorController> logger)
    {
        _reversals = reversals ?? throw new ArgumentNullException(nameof(reversals));
        _compensation = compensation ?? throw new ArgumentNullException(nameof(compensation));
        _sagas = sagas ?? throw new ArgumentNullException(nameof(sagas));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Reversals that are not finished, oldest first. The board.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(AZOAResult<IReadOnlyList<FiatReversalView>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListOpen([FromQuery] int? limit, CancellationToken ct)
    {
        var rows = await _reversals.ListOpenAsync(limit ?? DefaultListLimit, ct);
        if (rows.IsError)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, rows);

        return Ok(AZOAResult<IReadOnlyList<FiatReversalView>>.Success(
            rows.Result!.Select(FiatReversalView.From).ToList(), "Success"));
    }

    /// <summary>One reversal in full.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(AZOAResult<FiatReversalView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var read = await _reversals.GetAsync(id, ct);
        if (read.IsError)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, read);
        if (read.Result is null)
            return NotFound(new { message = $"No reversal '{id}'." });

        return Ok(AZOAResult<FiatReversalView>.Success(FiatReversalView.From(read.Result), "Success"));
    }

    /// <summary>Every reversal recorded against one account — the context behind a pause.</summary>
    [HttpGet("accounts/{userId:guid}")]
    [ProducesResponseType(typeof(AZOAResult<IReadOnlyList<FiatReversalView>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListForAccount(
        Guid userId, [FromQuery] int? limit, CancellationToken ct)
    {
        var rows = await _reversals.ListForUserAsync(userId, limit ?? DefaultListLimit, ct);
        if (rows.IsError)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, rows);

        return Ok(AZOAResult<IReadOnlyList<FiatReversalView>>.Success(
            rows.Result!.Select(FiatReversalView.From).ToList(), "Success"));
    }

    /// <summary>
    /// Runs the unwind now instead of waiting for the saga's next attempt. Idempotent: it takes
    /// back only what is still outstanding, and a move whose outcome cannot be established parks
    /// the reversal rather than repeating it.
    /// </summary>
    [Authorize(Policy = "RecentNodeOperatorSession")]
    [HttpPost("{id}/recover")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(AZOAResult<FiatReversalView>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Recover(string id, CancellationToken ct)
    {
        var outcome = await _compensation.RecoverAsync(id, ct);
        if (outcome.IsError)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, outcome);

        return Ok(AZOAResult<FiatReversalView>.Success(
            FiatReversalView.From(outcome.Result!.Row),
            outcome.Result.Definite
                ? "The unwind ran."
                : outcome.Result.IndeterminateReason ?? "The outcome could not be established."));
    }

    /// <summary>
    /// Closes the reversal: whatever is still outstanding becomes a recorded platform loss and,
    /// per the flow's policy, a pause on the account.
    /// </summary>
    [Authorize(Policy = "RecentNodeOperatorSession")]
    [HttpPost("{id}/settle")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(AZOAResult<FiatReversalView>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Settle(string id, CancellationToken ct)
    {
        var outcome = await _compensation.SettleShortfallAsync(id, ct);
        if (outcome.IsError)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, outcome);

        return Ok(AZOAResult<FiatReversalView>.Success(
            FiatReversalView.From(outcome.Result!.Row), "The reversal is closed."));
    }

    /// <summary>
    /// Settles a reversal parked on an ambiguous move with chain evidence: the node re-reads the
    /// move's transaction, records it landed or absent, and re-queues the unwind.
    /// </summary>
    [Authorize(Policy = "RecentNodeOperatorSession")]
    [HttpPost("{id}/resolve")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(AZOAResult<FiatReversalView>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Resolve(string id, [FromBody] ClearReversalFlagRequest? body, CancellationToken ct)
    {
        if (!HttpContext.TryGetOperatorActor(out var operatorActor))
            return Forbid();

        var resolved = await _compensation.ResolveParkedAsync(id, operatorActor.SubjectUserId, body?.Reason ?? string.Empty, ct);
        if (resolved.IsError)
            return resolved.Code switch
            {
                AzoaErrorCodes.NotFound => NotFound(resolved),
                AzoaErrorCodes.InvalidRequest => BadRequest(resolved),
                AzoaErrorCodes.Conflict => Conflict(resolved),
                _ => StatusCode(StatusCodes.Status503ServiceUnavailable, resolved),
            };

        var row = resolved.Result!;
        await _sagas.StartAsync(
            FiatCompensationSaga.Name,
            FiatCompensationSaga.CorrelationKeyFor(row.Id),
            new FiatCompensationPayload(row.Id, row.ChargeId, row.LotId),
            ct);

        return Ok(AZOAResult<FiatReversalView>.Success(
            FiatReversalView.From(row), "The move was resolved and the unwind was queued."));
    }

    /// <summary>
    /// Starts (or restarts) the durable unwind for a reversal whose saga was never enqueued or
    /// was cancelled. The saga is idempotent per reversal, so a second run replays rather than
    /// recovering twice.
    /// </summary>
    [Authorize(Policy = "RecentNodeOperatorSession")]
    [HttpPost("{id}/restart")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(AZOAResult<FiatReversalView>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Restart(string id, CancellationToken ct)
    {
        var read = await _reversals.GetAsync(id, ct);
        if (read.IsError)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, read);
        if (read.Result is not { } row)
            return NotFound(new { message = $"No reversal '{id}'." });

        await _sagas.StartAsync(
            FiatCompensationSaga.Name,
            FiatCompensationSaga.CorrelationKeyFor(row.Id),
            new FiatCompensationPayload(row.Id, row.ChargeId, row.LotId),
            ct);

        _logger.LogWarning(
            "An operator restarted the unwind for reversal {ReversalId}.", row.Id);

        return Ok(AZOAResult<FiatReversalView>.Success(
            FiatReversalView.From(row), "The unwind was queued."));
    }

    /// <summary>
    /// Lifts the pause on an account. Stamped with who lifted it and when — this is the one verb
    /// here that gives an account its ability to move value back.
    /// </summary>
    [Authorize(Policy = "RecentNodeOperatorSession")]
    [HttpPost("{id}/clear-flag")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(AZOAResult<FiatReversalView>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ClearFlag(
        string id, [FromBody] ClearReversalFlagRequest? body, CancellationToken ct)
    {
        if (!HttpContext.TryGetOperatorActor(out var operatorActor))
            return Unauthorized(new { message = "An identified operator is required to lift a pause." });

        var read = await _reversals.GetAsync(id, ct);
        if (read.IsError)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, read);
        if (read.Result is not { } row)
            return NotFound(new { message = $"No reversal '{id}'." });

        if (!row.AccountFlagged)
            return Ok(AZOAResult<FiatReversalView>.Success(
                FiatReversalView.From(row), "This reversal does not pause an account."));

        var now = DateTimeOffset.UtcNow;
        row.AccountFlagged = false;
        row.FlagClearedAt = now;
        row.FlagClearedBy = SurrealLink.ToLink(AZOA.WebAPI.Persistence.SurrealDb.Models.User.SchemaNameConst, operatorActor.SubjectUserId.ToString("N"));
        row.FlagReason = string.IsNullOrWhiteSpace(body?.Reason)
            ? row.FlagReason
            : body!.Reason!.Trim();

        var expected = row.Version;
        row.Version = expected + 1;
        row.UpdatedAt = now;

        var saved = await _reversals.UpdateIfVersionAsync(row, expected, ct);
        if (saved.IsError)
        {
            row.Version = expected;
            return StatusCode(StatusCodes.Status409Conflict, saved);
        }

        _logger.LogWarning(
            "Operator {OperatorId} lifted the pause on the account behind reversal {ReversalId}.",
            operatorActor.AuditActor, row.Id);

        return Ok(AZOAResult<FiatReversalView>.Success(
            FiatReversalView.From(saved.Result!), "The pause was lifted."));
    }
}

/// <summary>Why an operator lifted the pause on an account.</summary>
public sealed class ClearReversalFlagRequest
{
    /// <summary>Free text kept on the row so the decision is auditable.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// One reversal as an operator sees it. Amounts are exact decimal strings; nothing here is a
/// float, because a rounded figure on a financial board is worse than no figure.
/// </summary>
public sealed class FiatReversalView
{
    public string Id { get; init; } = string.Empty;

    /// <summary>"Refund" or "Dispute".</summary>
    public string Kind { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string? StatusReason { get; init; }

    /// <summary>The ambiguous move this reversal is parked on; resolve it with chain evidence.</summary>
    public string? PendingMoveKey { get; init; }

    /// <summary>The provider event that last moved this reversal.</summary>
    public string LastEventType { get; init; } = string.Empty;

    public string ChargeId { get; init; } = string.Empty;

    public string? SubjectUserId { get; init; }

    public string? FlowPurpose { get; init; }

    /// <summary>The money taken back, in minor units of <see cref="CurrencyCode"/>.</summary>
    public long AmountMinorUnits { get; init; }

    /// <summary>The original payment, in the same minor units.</summary>
    public long SourceAmountMinorUnits { get; init; }

    public string CurrencyCode { get; init; } = string.Empty;

    public bool IsPartial { get; init; }

    /// <summary>False for a dispute nobody has debited yet.</summary>
    public bool FundsWithdrawn { get; init; }

    public string? AssetId { get; init; }

    public string TargetQuantityBaseUnits { get; init; } = "0";

    public string RecoveredQuantityBaseUnits { get; init; } = "0";

    public string AbsorbedQuantityBaseUnits { get; init; } = "0";

    public string ReinstatementOwedBaseUnits { get; init; } = "0";

    /// <summary>Which rung of the unwind answered.</summary>
    public string RecoveryPath { get; init; } = string.Empty;

    public bool AccountFlagged { get; init; }

    public string? FlagReason { get; init; }

    public DateTimeOffset? FlagClearedAt { get; init; }

    public long AttemptCount { get; init; }

    public DateTimeOffset OpenedAt { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    public DateTimeOffset? ReinstatedAt { get; init; }

    /// <summary>Projects the durable row. Card details never existed on it and none appear here.</summary>
    public static FiatReversalView From(FiatReversal row) => new()
    {
        Id = row.Id,
        Kind = row.Kind,
        Status = row.Status,
        StatusReason = row.StatusReason,
        PendingMoveKey = row.PendingMoveKey,
        LastEventType = row.LastEventType,
        ChargeId = row.ChargeId,
        SubjectUserId = SurrealLink.FromLink(row.SubjectUserId),
        FlowPurpose = row.FlowPurpose,
        AmountMinorUnits = row.AmountMinorUnits,
        SourceAmountMinorUnits = row.SourceAmountMinorUnits,
        CurrencyCode = row.CurrencyCode,
        IsPartial = row.IsPartial,
        FundsWithdrawn = row.FundsWithdrawn,
        AssetId = row.AssetId,
        TargetQuantityBaseUnits = row.TargetQuantityBaseUnits,
        RecoveredQuantityBaseUnits = row.RecoveredQuantityBaseUnits,
        AbsorbedQuantityBaseUnits = row.AbsorbedQuantityBaseUnits,
        ReinstatementOwedBaseUnits = row.ReinstatementOwedBaseUnits,
        RecoveryPath = row.RecoveryPath,
        AccountFlagged = row.AccountFlagged,
        FlagReason = row.FlagReason,
        FlagClearedAt = row.FlagClearedAt,
        AttemptCount = row.AttemptCount,
        OpenedAt = row.OpenedAt,
        ResolvedAt = row.ResolvedAt,
        ReinstatedAt = row.ReinstatedAt,
    };
}
