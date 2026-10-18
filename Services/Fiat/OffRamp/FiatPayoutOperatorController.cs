// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Sagas;
using SurrealForge.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Services.NodeAdmin;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>One cash-out as an operator needs to see it: every figure, and what is owed.</summary>
public sealed record FiatPayoutView(
    string Id,
    string Status,
    string? StatusReason,
    string SubjectUserId,
    string? TenantId,
    long AmountMinorUnits,
    string CurrencyCode,
    string QuantityBaseUnits,
    string AgedQuantityBaseUnits,
    string UnitsOwedBaseUnits,
    long ReversedMinorUnits,
    int ConsumedLotCount,
    string? ConnectedAccountId,
    string? ProviderTransferId,
    string? UnitsCollectedTransactionId,
    string? UnitsReturnedTransactionId,
    string? ReversalId,
    long AttemptCount,
    DateTimeOffset RequestedAt,
    DateTimeOffset? UnitsCollectedAt,
    DateTimeOffset? PaidAt,
    DateTimeOffset? ReversedAt,
    string? ProviderPayoutId = null)
{
    /// <summary>Projects one row for the operator board.</summary>
    public static FiatPayoutView From(FiatPayout row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new FiatPayoutView(
            row.Id,
            row.Status,
            row.StatusReason,
            SurrealLink.FromLink(row.SubjectUserId)!,
            SurrealLink.FromLink(row.TenantId),
            row.AmountMinorUnits,
            row.CurrencyCode,
            row.QuantityBaseUnits,
            row.AgedQuantityBaseUnits,
            row.UnitsOwedBaseUnits,
            row.ReversedMinorUnits,
            row.ReadConsumedLots().Count,
            row.ConnectedAccountId,
            row.ProviderTransferId,
            row.UnitsCollectedTransactionId,
            row.UnitsReturnedTransactionId,
            SurrealLink.FromLink(row.ReversalId),
            row.AttemptCount,
            row.RequestedAt,
            row.UnitsCollectedAt,
            row.PaidAt,
            row.ReversedAt,
            row.ProviderPayoutId);
    }
}

/// <summary>
/// The operator's view of money on its way out, and the three things a person can do about one:
/// run the next step now, give the units back, or hand the whole thing to a human queue.
///
/// <para>Guarded by the hardened <c>Operator</c> policy (JWT floor plus an explicit operator
/// capability — an API key can never reach it), the same gate the reversal board and the saga
/// dead-letter board use. Every verb here moves money or closes a debt.</para>
///
/// <para>Colocated with the pipeline rather than under <c>Controllers/</c> because that directory
/// belongs to other items in this wave; MVC discovers controllers by type, not by folder, so the
/// route is identical either way and moving the file later changes nothing.</para>
/// </summary>
[ApiController]
[Route("api/admin/fiat-payouts")]
[Authorize(Policy = "Operator")]
public sealed class FiatPayoutOperatorController : ControllerBase
{
    private const int DefaultListLimit = 100;

    private readonly IFiatPayoutLedger _payouts;
    private readonly IOffRamp _offRamp;
    private readonly ISagaCoordinator _sagas;
    private readonly IPayoutAccountService _accounts;
    private readonly ILogger<FiatPayoutOperatorController> _logger;

    public FiatPayoutOperatorController(
        IFiatPayoutLedger payouts,
        IOffRamp offRamp,
        ISagaCoordinator sagas,
        IPayoutAccountService accounts,
        ILogger<FiatPayoutOperatorController> logger)
    {
        _payouts = payouts ?? throw new ArgumentNullException(nameof(payouts));
        _offRamp = offRamp ?? throw new ArgumentNullException(nameof(offRamp));
        _sagas = sagas ?? throw new ArgumentNullException(nameof(sagas));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// RV-L1-01: resolves a payout-account setup stuck past its Stripe replay window — attaches the account Stripe
    /// holds for the claim, or releases the claim once Stripe proves none exists. See AGENTS.md §account-recovery.
    /// </summary>
    [Authorize(Policy = "RecentNodeOperatorSession")]
    [HttpPost("accounts/{userId:guid}/setup/resolve")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(AZOAResult<PayoutAccountSetupResolution>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResolveAccountSetup(Guid userId, [FromBody] FiatPayoutOperatorReason body, CancellationToken ct)
    {
        if (!HttpContext.TryGetOperatorActor(out var operatorActor)) return Forbid();
        var actor = operatorActor.SubjectUserId;
        var result = await _accounts.ResolveStuckSetupAsync(userId, actor, body?.Reason ?? string.Empty, ct);
        if (!result.IsError)
            return Ok(result);
        return result.Code switch
        {
            AzoaErrorCodes.NotFound => NotFound(result),
            AzoaErrorCodes.InvalidRequest => BadRequest(result),
            _ => Conflict(result),
        };
    }

    /// <summary>Cash-outs in flight, reversed, or waiting on a person. Newest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(AZOAResult<IReadOnlyList<FiatPayoutView>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListBoard([FromQuery] int? limit, CancellationToken ct)
    {
        var rows = await _payouts.ListBoardAsync(limit ?? DefaultListLimit, ct);
        if (rows.IsError)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, rows);

        return Ok(AZOAResult<IReadOnlyList<FiatPayoutView>>.Success(
            rows.Result!.Select(FiatPayoutView.From).ToList(), "Success"));
    }

    /// <summary>One cash-out in full.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(AZOAResult<FiatPayoutView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var read = await _payouts.GetAsync(id, ct);
        if (read.IsError)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, read);
        if (read.Result is null)
            return NotFound(new { message = $"No cash-out '{id}'." });

        return Ok(AZOAResult<FiatPayoutView>.Success(FiatPayoutView.From(read.Result), "Success"));
    }

    /// <summary>Every cash-out one account has asked for — the context behind a stuck one.</summary>
    [HttpGet("accounts/{userId:guid}")]
    [ProducesResponseType(typeof(AZOAResult<IReadOnlyList<FiatPayoutView>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListForAccount(
        Guid userId, [FromQuery] int? limit, CancellationToken ct)
    {
        var rows = await _payouts.ListForUserAsync(userId, limit ?? DefaultListLimit, ct);
        if (rows.IsError)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, rows);

        return Ok(AZOAResult<IReadOnlyList<FiatPayoutView>>.Success(
            rows.Result!.Select(FiatPayoutView.From).ToList(), "Success"));
    }

    /// <summary>
    /// Runs the next step now instead of waiting for the saga. Idempotent: each leg is claimed on
    /// the idempotency ledger before it moves anything, and a move whose outcome cannot be
    /// established parks the cash-out rather than repeating it.
    /// </summary>
    [Authorize(Policy = "RecentNodeOperatorSession")]
    [HttpPost("{id}/advance")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(AZOAResult<FiatPayoutView>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Advance(string id, CancellationToken ct)
    {
        var read = await _payouts.GetAsync(id, ct);
        if (read.IsError)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, read);
        if (read.Result is null)
            return NotFound(new { message = $"No cash-out '{id}'." });

        // Which leg is next is decided by the ROW, never by the caller: an operator who could
        // choose "send the money" on a cash-out whose units were never collected would be able to
        // pay for units the platform does not hold.
        var outcome = read.Result.Status switch
        {
            FiatPayoutStatuses.Requested => await _offRamp.CollectUnitsAsync(id, ct),
            FiatPayoutStatuses.UnitsCollected => await _offRamp.SendMoneyAsync(id, ct),
            FiatPayoutStatuses.BankPending => await _offRamp.SendMoneyAsync(id, ct),
            _ => null,
        };

        if (outcome is null)
            return Conflict(new { message = $"A cash-out that is {read.Result.Status} has no next step." });

        if (outcome.IsError)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, outcome);

        return Ok(AZOAResult<FiatPayoutView>.Success(
            FiatPayoutView.From(outcome.Result!.Row),
            outcome.Result.Definite
                ? "The step ran."
                : outcome.Result.IndeterminateReason ?? "The outcome could not be established."));
    }

    /// <summary>
    /// Gives the units back and closes the cash-out. The honest undo for one that will never be
    /// sent — and the only one that leaves the person whole.
    /// </summary>
    [Authorize(Policy = "RecentNodeOperatorSession")]
    [HttpPost("{id}/return")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(AZOAResult<FiatPayoutView>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Return(
        string id, [FromBody] FiatPayoutOperatorReason? body, CancellationToken ct)
    {
        var reason = string.IsNullOrWhiteSpace(body?.Reason)
            ? "Closed from the operator board."
            : body!.Reason!.Trim();

        var outcome = await _offRamp.AbandonAsync(id, reason, ct);
        if (outcome.IsError)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, outcome);

        _logger.LogWarning(
            "Cash-out {PayoutId} was unwound from the operator board: {Reason}", id, reason);

        return Ok(AZOAResult<FiatPayoutView>.Success(
            FiatPayoutView.From(outcome.Result!.Row),
            outcome.Result.Definite
                ? "The units were given back."
                : outcome.Result.IndeterminateReason ?? "The outcome could not be established."));
    }

    /// <summary>Returns units only after the failed bank payout's funding is recovered.</summary>
    [Authorize(Policy = "RecentNodeOperatorSession")]
    [HttpPost("{id}/recover")]
    [EnableRateLimiting("financial")]
    public async Task<IActionResult> Recover(string id, [FromBody] FiatPayoutOperatorReason body, CancellationToken ct)
    {
        if (!HttpContext.TryGetOperatorActor(out var operatorActor)) return Forbid();
        var actor = operatorActor.SubjectUserId;
        var result = await _offRamp.RecoverFailedBankAsync(id, actor, body.Reason ?? string.Empty, ct);
        return result.IsError ? Conflict(result) : Ok(AZOAResult<FiatPayoutView>.Success(FiatPayoutView.From(result.Result!)));
    }

    /// <summary>
    /// Audited exit for a parked or reversed cash-out: <c>record-paid</c> or <c>return-units</c>,
    /// each allowed only when re-read Stripe (and chain) evidence supports it.
    /// </summary>
    [Authorize(Policy = "RecentNodeOperatorSession")]
    [HttpPost("{id}/resolve")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(AZOAResult<FiatPayoutView>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Resolve(string id, [FromBody] FiatPayoutResolveRequest body, CancellationToken ct)
    {
        if (!HttpContext.TryGetOperatorActor(out var operatorActor)) return Forbid();
        var actor = operatorActor.SubjectUserId;
        PayoutResolution? resolution = body?.Resolution switch
        {
            "record-paid" => PayoutResolution.RecordPaid,
            "return-units" => PayoutResolution.ReturnUnits,
            _ => null,
        };
        if (resolution is null)
            return BadRequest(AZOAResult<FiatPayoutView>.FailureWithCode(
                "Resolution must be 'record-paid' or 'return-units'.", AzoaErrorCodes.InvalidRequest));

        var result = await _offRamp.ResolveForOperatorAsync(id, resolution.Value, actor, body!.Reason ?? string.Empty, ct);
        if (!result.IsError)
            return Ok(AZOAResult<FiatPayoutView>.Success(FiatPayoutView.From(result.Result!), "Resolved."));
        return result.Code switch
        {
            AzoaErrorCodes.NotFound => NotFound(result),
            AzoaErrorCodes.InvalidRequest => BadRequest(result),
            AzoaErrorCodes.Conflict => Conflict(result),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
        };
    }

    /// <summary>Parks the cash-out for a person, with the reason recorded on the row.</summary>
    [Authorize(Policy = "RecentNodeOperatorSession")]
    [HttpPost("{id}/park")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(AZOAResult<FiatPayoutView>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Park(
        string id, [FromBody] FiatPayoutOperatorReason? body, CancellationToken ct)
    {
        var reason = string.IsNullOrWhiteSpace(body?.Reason)
            ? "Held for review from the operator board."
            : body!.Reason!.Trim();

        var parked = await _offRamp.ParkForOperatorAsync(id, reason, ct);
        if (parked.IsError)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, parked);

        return Ok(AZOAResult<FiatPayoutView>.Success(
            FiatPayoutView.From(parked.Result!), "The cash-out is held for review."));
    }

    /// <summary>
    /// Queues the durable saga for a cash-out that has stopped moving — the recovery when the
    /// saga could not be started at request time.
    /// </summary>
    [Authorize(Policy = "RecentNodeOperatorSession")]
    [HttpPost("{id}/restart")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(AZOAResult<FiatPayoutView>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Restart(string id, CancellationToken ct)
    {
        var read = await _payouts.GetAsync(id, ct);
        if (read.IsError)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, read);
        if (read.Result is null)
            return NotFound(new { message = $"No cash-out '{id}'." });

        if (FiatPayoutStatuses.IsSettled(read.Result.Status))
            return Conflict(new { message = $"A cash-out that is {read.Result.Status} is finished." });

        try
        {
            await _sagas.StartAsync(
                OffRampSaga.Name,
                OffRampSaga.CorrelationKeyFor(read.Result.Id),
                new OffRampCollectPayload(read.Result.Id, read.Result.SubjectUserId),
                ct);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Cash-out {PayoutId} could not be queued.", id);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                AZOAResult<FiatPayoutView>.Failure("The cash-out could not be queued."));
        }

        return Ok(AZOAResult<FiatPayoutView>.Success(
            FiatPayoutView.From(read.Result), "The cash-out was queued."));
    }
}

/// <summary>The operator's own words for why they acted. Recorded verbatim on the row.</summary>
public sealed class FiatPayoutOperatorReason
{
    public string? Reason { get; set; }
}

/// <summary>Body of <c>POST /api/admin/fiat-payouts/{id}/resolve</c>.</summary>
public sealed class FiatPayoutResolveRequest
{
    /// <summary><c>record-paid</c> or <c>return-units</c>.</summary>
    public string? Resolution { get; set; }

    /// <summary>Required, 1–512 characters; appended to the cash-out's audit.</summary>
    public string? Reason { get; set; }
}
