// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SurrealForge.Client;

namespace AZOA.WebAPI.Services.Fiat;

/// <summary>Operator visibility and replay for verified payment events; see AGENTS.md.</summary>
[ApiController]
[Route("api/admin/fiat-events")]
// OI-65, and the same reason as the on-ramp lot board beside it: api/operator/ is the only prefix
// the console's BFF proxies, so without this address the board is reachable only with a JWT and a
// curl. Same Operator policy on both routes.
[Route("api/operator/fiat-events")]
[Authorize(Policy = "Operator")]
public sealed class FiatEventOperatorController : ControllerBase
{
    private const int DefaultListLimit = 100;

    private readonly IStripeWebhookInbox _inbox;
    private readonly ILogger<FiatEventOperatorController> _logger;

    public FiatEventOperatorController(
        IStripeWebhookInbox inbox, ILogger<FiatEventOperatorController> logger)
    {
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Deliveries no handler finished, newest first. The board.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(AZOAResult<IReadOnlyList<FiatEventView>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListNeedingOperator([FromQuery] int? limit, CancellationToken ct)
    {
        var rows = await _inbox.ListNeedingOperatorAsync(limit ?? DefaultListLimit, ct);
        if (rows.IsError)
        {
            // 503, never an empty 200: an unavailable board and an empty one are different
            // answers, and only one of them is safe to act on.
            return StatusCode(StatusCodes.Status503ServiceUnavailable, rows);
        }

        var board = rows.Result!.Select(FiatEventView.From).ToList();
        var abandoned = board.Count(view => view.ProviderWasToldToStopRetrying);
        if (abandoned > 0)
        {
            _logger.LogWarning(
                "An operator listed {Count} payment delivery/deliveries this node answered 202 and never finished.",
                abandoned);
        }

        return Ok(AZOAResult<IReadOnlyList<FiatEventView>>.Success(board, "Success"));
    }

    /// <summary>Replays a verified delivery through the authoritative handlers.</summary>
    [HttpPost("{id}/replay")]
    [EnableRateLimiting("financial")]
    public async Task<IActionResult> Replay(string id, [FromServices] FiatEventRecovery recovery, CancellationToken ct)
    {
        var result = await recovery.ReplayAsync(id, ct);
        if (result.IsError || result.Result is null) return Conflict(result);
        _logger.LogWarning("Operator {Actor} replayed fiat event {EventId}; status {Status}.",
            User.Identity?.Name, id, result.Result.Status);
        return Ok(AZOAResult<FiatEventView>.Success(FiatEventView.From(result.Result)));
    }
}

/// <summary>
/// One inbox row as an operator sees it. The raw body was never stored, so nothing card-adjacent
/// can appear here; what is projected is identity, correlation and the delivery's disposition.
/// </summary>
public sealed class FiatEventView
{
    public string Id { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    /// <summary>The provider's own event id — what an operator searches for on the provider side.</summary>
    public string ProviderEventId { get; init; } = string.Empty;

    public string EventType { get; init; } = string.Empty;

    public string? ApiVersion { get; init; }

    public string Environment { get; init; } = string.Empty;

    public bool Livemode { get; init; }

    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// True when this node already answered "stop retrying" for a delivery it never finished, so
    /// only a person will move it. False while the provider's own retry still owns the delivery.
    /// </summary>
    public bool ProviderWasToldToStopRetrying { get; init; }

    public long AttemptCount { get; init; }

    /// <summary>Why the last attempt failed, as this node recorded it.</summary>
    public string? LastError { get; init; }

    /// <summary>The funding intent this delivery concerns, when it could be correlated.</summary>
    public string? IntentId { get; init; }

    public string? SubjectUserId { get; init; }

    public string? CheckoutSessionId { get; init; }

    public string? PaymentIntentId { get; init; }

    public string? ChargeId { get; init; }

    /// <summary>SHA-256 of the exact bytes that were signature-verified.</summary>
    public string PayloadDigest { get; init; } = string.Empty;

    public DateTimeOffset? OccurredAt { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }

    public DateTimeOffset? AppliedAt { get; init; }

    /// <summary>Projects the durable row.</summary>
    public static FiatEventView From(FiatEvent row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new FiatEventView
        {
            Id = row.Id,
            Provider = row.Provider,
            ProviderEventId = row.ProviderEventId,
            EventType = row.EventType,
            ApiVersion = row.ApiVersion,
            Environment = row.Environment,
            Livemode = row.Livemode,
            Status = row.Status,
            ProviderWasToldToStopRetrying =
                FiatEventStatuses.ProviderWasToldToStopRetrying(row.Status, row.AttemptCount),
            AttemptCount = row.AttemptCount,
            LastError = row.LastError,
            IntentId = SurrealLink.FromLink(row.IntentId),
            SubjectUserId = SurrealLink.FromLink(row.SubjectUserId),
            CheckoutSessionId = row.CheckoutSessionId,
            PaymentIntentId = row.PaymentIntentId,
            ChargeId = row.ChargeId,
            PayloadDigest = row.PayloadDigest,
            OccurredAt = row.OccurredAt,
            ReceivedAt = row.ReceivedAt,
            AppliedAt = row.AppliedAt,
        };
    }
}
