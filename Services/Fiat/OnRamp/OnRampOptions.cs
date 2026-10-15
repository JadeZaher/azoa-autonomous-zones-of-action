// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Services.Fiat.OnRamp;

/// <summary>
/// Operator configuration for the fiat on-ramp saga. Bound from the <c>Fiat:OnRamp</c> section;
/// see <c>Services/Fiat/OnRamp/AGENTS.md</c>.
/// </summary>
public sealed class OnRampOptions
{
    /// <summary>Configuration section this binds from.</summary>
    public const string SectionName = "Fiat:OnRamp";

    /// <summary>Fewest issuance attempts one payment may consume before the lot is parked.</summary>
    public const int MinimumIssuanceAttempts = 1;

    /// <summary>Most issuance attempts one payment may consume before the lot is parked.</summary>
    public const int MaximumIssuanceAttempts = 20;

    /// <summary>
    /// The reserve partition on-ramped units are drawn from. The reserve REFUSES a draw against a
    /// partition nobody declared, so a wrong value here fails closed and is operator-fixable —
    /// it can never overdraw a partition that was earmarked for something else.
    /// </summary>
    public string ReservePartition { get; set; } = "on-ramp";

    /// <summary>
    /// Whether a payment driven by a tenant that has registered NO price of its own may be priced
    /// by the operator's node-wide default.
    /// </summary>
    /// <remarks>
    /// Off by default. Substituting one party's price for another's is a silent economic decision,
    /// and the refusal it replaces is visible and fixable (register the price, and the provider's
    /// own retry credits the payment).
    /// </remarks>
    public bool AllowNodeDefaultScheduleFallback { get; set; }

    /// <summary>
    /// How many times one cleared payment may be attempted before the lot is parked for an
    /// operator instead of being retried forever.
    /// </summary>
    /// <remarks>
    /// This is the bound that keeps the provider from retrying indefinitely AND keeps it from
    /// giving up silently: once it is spent the lot is written <c>Stuck</c> with a reason, the
    /// provider is told to stop, and the charge is visible in the node's own data as money that
    /// was taken and not credited.
    /// </remarks>
    public int MaxIssuanceAttempts { get; set; } = 5;

    /// <summary>The effective attempt bound, clamped to the range above.</summary>
    public int EffectiveMaxIssuanceAttempts =>
        Math.Clamp(MaxIssuanceAttempts, MinimumIssuanceAttempts, MaximumIssuanceAttempts);
}
