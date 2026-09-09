// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AZOA.WebAPI.Services.Fiat.OnRamp;

/// <summary>
/// The operator's view of money that was taken and never became funds in somebody's account.
///
/// <para>Every row in <c>on_ramp_lot</c> is a payment the provider confirmed as cleared — the lot
/// is claimed only after the amount is re-read server-side — so a lot that is not Issued and not
/// Reversed is a person who paid and received nothing. Each of those outcomes also answers the
/// provider 202, telling it to stop retrying
/// (<c>Services/Fiat/OnRamp/OnRampSettlementHandler.cs</c>), which is exactly why the durable row
/// needs a reader: without one the node deliberately ends the conversation about a charge nobody
/// can see.</para>
///
/// <para>Guarded by the hardened <c>Operator</c> policy — a JWT floor plus an explicit operator
/// capability, never an API key — the same gate the reversal board and the platform-unit surface
/// use. Everything here is a read, and it reads other people's payment records.</para>
///
/// <para>Colocated with the on-ramp rather than under <c>Controllers/</c> because that directory
/// belongs to other items in this wave; MVC discovers controllers by type, not by folder, so the
/// route is identical either way and moving the file later changes nothing.</para>
/// </summary>
[ApiController]
[Route("api/admin/on-ramp-lots")]
[Authorize(Policy = "Operator")]
public sealed class OnRampLotOperatorController : ControllerBase
{
    private const int DefaultListLimit = 100;

    private readonly IOnRampLotLedger _lots;
    private readonly ILogger<OnRampLotOperatorController> _logger;

    public OnRampLotOperatorController(
        IOnRampLotLedger lots, ILogger<OnRampLotOperatorController> logger)
    {
        _lots = lots ?? throw new ArgumentNullException(nameof(lots));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Lots a person has to resolve: refused, stuck, or stranded pending. The board.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(AZOAResult<IReadOnlyList<OnRampLotView>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListNeedingOperator([FromQuery] int? limit, CancellationToken ct)
    {
        var rows = await _lots.ListNeedingOperatorAsync(limit ?? DefaultListLimit, ct);
        if (rows.IsError)
        {
            // 503, never an empty 200: "nobody is owed money" must never be what a failed read
            // looks like.
            return StatusCode(StatusCodes.Status503ServiceUnavailable, rows);
        }

        var board = rows.Result!.Select(OnRampLotView.From).ToList();
        if (board.Count > 0)
        {
            _logger.LogWarning(
                "An operator listed {Count} on-ramp lot(s) where a payment cleared and nothing was credited.",
                board.Count);
        }

        return Ok(AZOAResult<IReadOnlyList<OnRampLotView>>.Success(board, "Success"));
    }

    /// <summary>The lot one funding intent settled into — the row behind a person's complaint.</summary>
    [HttpGet("intents/{intentId}")]
    [ProducesResponseType(typeof(AZOAResult<OnRampLotView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByIntent(string intentId, CancellationToken ct)
    {
        // The intent id is hashed into a record id downstream, so it is parsed before it is used
        // rather than trimmed and hoped over.
        if (!Guid.TryParse(intentId, out var parsed) || parsed == Guid.Empty)
            return BadRequest(new { message = "A funding intent id is required." });

        var read = await _lots.GetByIntentAsync(parsed.ToString("N"), ct);
        if (read.IsError)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, read);
        if (read.Result is not { } row)
            return NotFound(new { message = $"No on-ramp lot settled funding intent '{parsed:N}'." });

        return Ok(AZOAResult<OnRampLotView>.Success(OnRampLotView.From(row), "Success"));
    }

    /// <summary>What one person actually received, oldest first — the context behind a claim.</summary>
    [HttpGet("accounts/{userId:guid}/issued")]
    [ProducesResponseType(typeof(AZOAResult<IReadOnlyList<OnRampLotView>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListIssuedForAccount(Guid userId, CancellationToken ct)
    {
        var rows = await _lots.ListIssuedForUserAsync(userId, ct);
        if (rows.IsError)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, rows);

        return Ok(AZOAResult<IReadOnlyList<OnRampLotView>>.Success(
            rows.Result!.Select(OnRampLotView.From).ToList(), "Success"));
    }
}

/// <summary>
/// One fiat-funded issuance as an operator sees it. Quantities are exact decimal strings and the
/// money is minor units of <see cref="CurrencyCode"/>; nothing here is a float, because a rounded
/// figure on a financial board is worse than no figure.
/// </summary>
public sealed class OnRampLotView
{
    public string Id { get; init; } = string.Empty;

    /// <summary>The node's own funding intent this payment settled.</summary>
    public string IntentId { get; init; } = string.Empty;

    public string SubjectUserId { get; init; } = string.Empty;

    public string? TenantId { get; init; }

    public string ChainType { get; init; } = string.Empty;

    public string AssetId { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    /// <summary>Why the lot is where it is. Always present once it is parked.</summary>
    public string? StatusReason { get; init; }

    /// <summary>
    /// True when this payment cleared and the person still holds nothing for it. A lot exists only
    /// after the money is confirmed taken, so every status but Issued (credited) and Reversed
    /// (given back) is somebody out of pocket.
    /// </summary>
    public bool ChargedAndUncredited { get; init; }

    /// <summary>
    /// True when the lot is parked at a status the node will never move on its own — refused or
    /// stuck. Only a person credits or refunds it from here.
    /// </summary>
    public bool NeedsAPersonToMoveIt { get; init; }

    /// <summary>The money the provider confirmed, in minor units of <see cref="CurrencyCode"/>.</summary>
    public long FiatMinorUnits { get; init; }

    public string CurrencyCode { get; init; } = string.Empty;

    /// <summary>Units the person received, net of the node fee.</summary>
    public string QuantityBaseUnits { get; init; } = "0";

    public string GrossQuantityBaseUnits { get; init; } = "0";

    public string NodeFeeBaseUnits { get; init; } = "0";

    /// <summary>How much of this lot a cash-out has already consumed.</summary>
    public string ConsumedBaseUnits { get; init; } = "0";

    public string? PriceScheduleId { get; init; }

    public long PriceScheduleVersion { get; init; }

    /// <summary>The rate that converted the money, copied onto the lot when it was issued.</summary>
    public string UnitsPerMinorUnit { get; init; } = "0";

    /// <summary>Provider-side ids an operator needs to look the payment up. Operator-only.</summary>
    public string? SourceCheckoutSessionId { get; init; }

    public string? SourcePaymentIntentId { get; init; }

    public string? SourceChargeId { get; init; }

    /// <summary>The durable operation that moved the units, when one ran.</summary>
    public string? OperationId { get; init; }

    public long AttemptCount { get; init; }

    public DateTimeOffset? IssuedAt { get; init; }

    public DateTimeOffset? ReversalWindowEnds { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Projects the durable row. No card detail ever existed on it and none appears here.</summary>
    public static OnRampLotView From(OnRampLot row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new OnRampLotView
        {
            Id = row.Id,
            IntentId = row.IntentId,
            SubjectUserId = row.SubjectUserId,
            TenantId = row.TenantId,
            ChainType = row.ChainType,
            AssetId = row.AssetId,
            Status = row.Status,
            StatusReason = row.StatusReason,
            ChargedAndUncredited =
                !string.Equals(row.Status, OnRampLotStatuses.Issued, StringComparison.Ordinal)
                && !string.Equals(row.Status, OnRampLotStatuses.Reversed, StringComparison.Ordinal),
            NeedsAPersonToMoveIt = OnRampLotStatuses.NeedsOperator(row.Status),
            FiatMinorUnits = row.FiatMinorUnits,
            CurrencyCode = row.CurrencyCode,
            QuantityBaseUnits = row.QuantityBaseUnits,
            GrossQuantityBaseUnits = row.GrossQuantityBaseUnits,
            NodeFeeBaseUnits = row.NodeFeeBaseUnits,
            ConsumedBaseUnits = row.ConsumedBaseUnits,
            PriceScheduleId = row.PriceScheduleId,
            PriceScheduleVersion = row.PriceScheduleVersion,
            UnitsPerMinorUnit = row.UnitsPerMinorUnit,
            SourceCheckoutSessionId = row.SourceCheckoutSessionId,
            SourcePaymentIntentId = row.SourcePaymentIntentId,
            SourceChargeId = row.SourceChargeId,
            OperationId = row.OperationId,
            AttemptCount = row.AttemptCount,
            IssuedAt = row.IssuedAt,
            ReversalWindowEnds = row.ReversalWindowEnds,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
        };
    }
}
