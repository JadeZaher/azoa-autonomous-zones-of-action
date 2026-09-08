// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Fiat.Reversal;

/// <summary>
/// The READ SIDE of the account risk flag, and the reason the flag is a control rather than a
/// note on a screen.
///
/// <para>It wraps the D46 value gate — the check every value action already has to pass — and
/// adds one condition: an account carrying an unresolved reversal flag does not pass. That is
/// what stops the loop this pipeline exists to close, where somebody charges back a payment,
/// keeps the units, and immediately buys more with another card.</para>
///
/// <para>Wrapping the existing gate rather than adding a second check is deliberate. A new gate
/// would have to be called from every value path by hand, and the six-instance pattern in this
/// codebase is a control that nothing calls. This one is enforced wherever
/// <see cref="IRealValueKycGate"/> already is: the fiat on-ramp
/// (<c>OnRampSettlementHandler.ApplyAsync</c>) and the cross-chain bridge
/// (<c>CrossChainBridgeService</c>).</para>
///
/// <para>It fails CLOSED. A ledger it cannot read is a refusal, never an assumed "no flag" —
/// that default is precisely how a control becomes decorative.</para>
/// </summary>
public sealed class RiskHoldValueGate : IRealValueKycGate
{
    /// <summary>
    /// What a barred account is told. Consumer vocabulary on purpose: the person on the other
    /// side of this refusal is not a crypto user and does not need to hear about chargebacks,
    /// clawbacks or tokens to understand that their account is paused.
    /// </summary>
    public const string RiskHoldMessage =
        "This account is paused while a reversed payment is being resolved. "
        + "Contact support to continue.";

    /// <summary>What a caller is told when the node cannot establish whether the account is paused.</summary>
    public const string UnreadableMessage =
        "This account's standing could not be checked right now. Nothing was changed; try again shortly.";

    private readonly IRealValueKycGate _inner;
    private readonly IFiatReversalLedger _reversals;
    private readonly ILogger<RiskHoldValueGate> _logger;

    public RiskHoldValueGate(
        IRealValueKycGate inner,
        IFiatReversalLedger reversals,
        ILogger<RiskHoldValueGate> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _reversals = reversals ?? throw new ArgumentNullException(nameof(reversals));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<AZOAResult<bool>> RequireCurrentApprovalAsync(
        Guid userId, CancellationToken ct = default)
    {
        var hold = await RequireNoRiskHoldAsync(userId, ct);
        return hold ?? await _inner.RequireCurrentApprovalAsync(userId, ct);
    }

    /// <inheritdoc />
    public async Task<AZOAResult<bool>> RequireCurrentApprovalAsync(
        Guid userId, Guid tenantId, CancellationToken ct = default)
    {
        var hold = await RequireNoRiskHoldAsync(userId, ct);
        return hold ?? await _inner.RequireCurrentApprovalAsync(userId, tenantId, ct);
    }

    /// <summary>
    /// The refusal, or null when the account is clear. Checked BEFORE the identity gate so a
    /// barred account is refused even where identity would have passed.
    /// </summary>
    private async Task<AZOAResult<bool>?> RequireNoRiskHoldAsync(Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty)
            return null;

        var flagged = await _reversals.HasActiveFlagAsync(userId, ct);
        if (flagged.IsError)
        {
            _logger.LogError(
                "The reversal flag for account {UserId} could not be read ({Detail}); the value action "
                + "was refused rather than allowed on an unchecked account.",
                userId, flagged.Message);
            return AZOAResult<bool>.Failure(UnreadableMessage);
        }

        if (!flagged.Result)
            return null;

        _logger.LogWarning(
            "A value action for account {UserId} was refused: an unresolved reversal is flagged "
            + "against it.",
            userId);
        return AZOAResult<bool>.Failure(RiskHoldMessage);
    }
}
