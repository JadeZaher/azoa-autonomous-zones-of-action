// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Settings;

/// <summary>
/// KYC module configuration, bound from the <c>Kyc</c> section. The default
/// provider is the in-house manual reviewer, which needs no secrets. The
/// external-provider fields are a deploy-stub — populate them from the deploy
/// secret store and flip <see cref="Provider"/> to <c>"veriff"</c> to enable the
/// (currently stubbed) external adapter. See docs/NODE-HOST.md §8 (KYC provisioning).
/// </summary>
public sealed class KycSettings
{
    public const string SectionName = "Kyc";

    /// <summary>Selected provider: <c>"manual"</c> (default) or <c>"veriff"</c>.</summary>
    public string Provider { get; set; } = "manual";

    /// <summary>External provider API key — NEVER committed; supplied at deploy time.</summary>
    public string? VeriffApiKey { get; set; }

    /// <summary>External provider API base URL — supplied at deploy time.</summary>
    public string? VeriffBaseUrl { get; set; }

    /// <summary>Days until a submission expires; 0 = never expires.</summary>
    public int SubmissionExpiryDays { get; set; } = 0;
}
