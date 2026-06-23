// SPDX-License-Identifier: UNLICENSED
// Generic identity-verification (KYC) enums. Values mirror an industry-standard
// KYC lifecycle; no vendor or tenant branding is encoded here.

namespace AZOA.WebAPI.Models.Kyc;

/// <summary>
/// Lifecycle status of a single KYC submission.
/// Persisted as the string name (see the POCO <c>JsonStringEnumConverter</c>).
/// </summary>
public enum KycStatus
{
    /// <summary>Submitted, awaiting review.</summary>
    PENDING,

    /// <summary>Picked up by a reviewer / provider but not yet decided.</summary>
    IN_REVIEW,

    /// <summary>Identity verified — the gate opens for this avatar.</summary>
    APPROVED,

    /// <summary>Rejected — the avatar must re-submit to be verified.</summary>
    REJECTED,

    /// <summary>Lapsed past its expiry window.</summary>
    EXPIRED
}

/// <summary>
/// Which verification provider produced/owns a submission. The default
/// <see cref="MANUAL"/> provider is admin-review only; <see cref="VERIFF"/>
/// is a config-gated external provider (deploy-stub).
/// </summary>
public enum KycProvider
{
    /// <summary>In-house manual admin review (the default, no external secrets).</summary>
    MANUAL,

    /// <summary>External automated provider (config-gated; stub until provisioned).</summary>
    VERIFF
}

/// <summary>
/// The kind of identity document attached to a submission.
/// Persisted as the string name.
/// </summary>
public enum KycDocumentType
{
    GOVERNMENT_ID,
    PASSPORT,
    DRIVERS_LICENSE,
    SELFIE,
    PROOF_OF_ADDRESS
}
