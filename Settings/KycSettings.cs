// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Settings;

/// <summary>
/// KYC module configuration, bound from the <c>Kyc</c> section. The base
/// provider is unavailable. Development may explicitly select manual review;
/// external adapters fail closed until reviewed. See
/// docs/TENANT-CUSTODIAL-ONBOARDING.md.
/// </summary>
public sealed class KycSettings
{
    public const string SectionName = "Kyc";

    /// <summary>
    /// Selected provider: <c>manual</c>, <c>veriff</c>, or <c>hosted</c>.
    /// Unknown values select an unavailable provider rather than falling back.
    /// </summary>
    public string Provider { get; set; } = "unavailable";

    /// <summary>External provider API key — NEVER committed; supplied at deploy time.</summary>
    public string? VeriffApiKey { get; set; }

    /// <summary>External provider API base URL — supplied at deploy time.</summary>
    public string? VeriffBaseUrl { get; set; }

    /// <summary>Generic hosted-provider scaffold configuration.</summary>
    public HostedKycSettings Hosted { get; set; } = new();

    /// <summary>Operator-owned trust profile stamped onto each verification attempt.</summary>
    public KycApprovalPolicySettings ApprovalPolicy { get; set; } = new();

    /// <summary>Days until a submission expires; runtime clamps to 1-365.</summary>
    public int SubmissionExpiryDays { get; set; } = 30;

    /// <summary>Minutes a begin-session response may be replayed; runtime clamps to 1-1440.</summary>
    public int SessionExpiryMinutes { get; set; } = 30;
}

/// <summary>Fail-closed policy and assurance configuration for KYC approvals.</summary>
public sealed class KycApprovalPolicySettings
{
    /// <summary>Operator-controlled version; changing it requires re-verification.</summary>
    public string PolicyVersion { get; set; } = string.Empty;

    /// <summary>Provider-neutral assurance label required by the active policy.</summary>
    public string AssuranceLevel { get; set; } = string.Empty;

    /// <summary>Provider keys the operator accepts as identity authorities.</summary>
    public string[] TrustedProviderKeys { get; set; } = Array.Empty<string>();

    /// <summary>Explicit opt-in for the manual provider in Development only.</summary>
    public bool AllowManualInDevelopment { get; set; }
}

/// <summary>
/// Operator-owned configuration for the provider-neutral hosted adapter seam.
/// The current adapter is a fail-closed scaffold; values document the reviewed
/// implementation contract and must come from deployment configuration.
/// </summary>
public sealed class HostedKycSettings
{
    /// <summary>Public provider label returned in capability/session metadata.</summary>
    public string? ProviderName { get; set; }

    /// <summary>HTTPS API origin for the provider.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Provider credential; secret-store only.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Inbound webhook verification secret; secret-store only.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Relative session-creation endpoint for a reviewed adapter.</summary>
    public string SessionPath { get; set; } = "/sessions";

    /// <summary>Relative status endpoint template; <c>{sessionId}</c> is required.</summary>
    public string StatusPath { get; set; } = "/sessions/{sessionId}";
}
