// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>
/// Operator configuration for the fiat off-ramp. Bound from the <c>Fiat:OffRamp</c> section;
/// see <c>Services/Fiat/OffRamp/AGENTS.md</c>.
/// </summary>
public sealed class OffRampOptions
{
    /// <summary>Configuration section this binds from.</summary>
    public const string SectionName = "Fiat:OffRamp";

    /// <summary>Fewest payout attempts one cash-out may consume before it is parked.</summary>
    public const int MinimumPayoutAttempts = 1;

    /// <summary>Most payout attempts one cash-out may consume before it is parked.</summary>
    public const int MaximumPayoutAttempts = 20;

    /// <summary>
    /// Master switch for the cash-out path. OFF by default, and deliberately separate from
    /// <c>Fiat:Enabled</c>: money in and money out are approved by the provider separately
    /// (Connect platform review is its own external process), so a node that can take payments is
    /// not automatically a node that may send them.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The reserve partition cashed-out units go BACK to. It is the on-ramp's partition by
    /// default because that is where they were issued from: returning them anywhere else would
    /// let the on-ramp's declared availability shrink by every cash-out.
    /// </summary>
    public string ReservePartition { get; set; } = "on-ramp";

    /// <summary>Smallest cash-out this node will send, in minor units.</summary>
    /// <remarks>
    /// A floor exists because the provider charges per transfer: below it the fee is a
    /// meaningful fraction of the payment, which is a bad deal presented as a service.
    /// </remarks>
    public long MinimumAmountMinorUnits { get; set; } = 500;

    /// <summary>Largest cash-out this node will send in one go, in minor units.</summary>
    public long MaximumAmountMinorUnits { get; set; } = 200_000;

    /// <summary>
    /// How many times one cash-out may be attempted before it is parked for an operator instead
    /// of being retried forever.
    /// </summary>
    public int MaxPayoutAttempts { get; set; } = 5;

    /// <summary>The effective attempt bound, clamped to the range above.</summary>
    public int EffectiveMaxPayoutAttempts =>
        Math.Clamp(MaxPayoutAttempts, MinimumPayoutAttempts, MaximumPayoutAttempts);

    /// <summary>Two-letter country connected accounts are opened in.</summary>
    public string ConnectedAccountCountry { get; set; } = "US";

    /// <summary>
    /// Where the provider returns a person who finished setting up their payout account. Checked
    /// against <c>Fiat:Checkout:AllowedReturnOrigins</c> — the same allowlist, because the
    /// open-redirect risk on an onboarding page is the same one the checkout return page has.
    /// </summary>
    public string OnboardingReturnUrl { get; set; } = string.Empty;

    /// <summary>Where the provider sends a person whose onboarding link expired.</summary>
    public string OnboardingRefreshUrl { get; set; } = string.Empty;

    /// <summary>Configuration key NAMES that are missing for the cash-out path. Never values.</summary>
    public IReadOnlyList<string> MissingKeys()
    {
        var missing = new List<string>();

        if (!Enabled)
            missing.Add($"{SectionName}:Enabled");
        if (string.IsNullOrWhiteSpace(OnboardingReturnUrl))
            missing.Add($"{SectionName}:OnboardingReturnUrl");
        if (string.IsNullOrWhiteSpace(OnboardingRefreshUrl))
            missing.Add($"{SectionName}:OnboardingRefreshUrl");
        if (MinimumAmountMinorUnits <= 0 || MaximumAmountMinorUnits < MinimumAmountMinorUnits)
            missing.Add($"{SectionName}:MinimumAmountMinorUnits");

        return missing;
    }
}
