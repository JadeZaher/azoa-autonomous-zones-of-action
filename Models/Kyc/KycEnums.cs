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

    /// <summary>Identity verified — the gate opens for this user.</summary>
    APPROVED,

    /// <summary>Rejected — the user must re-submit to be verified.</summary>
    REJECTED,

    /// <summary>Lapsed past its expiry window.</summary>
    EXPIRED
}

/// <summary>
/// Which verification provider produced/owns a submission. The default
/// <see cref="MANUAL"/> provider is admin-review only. External values remain
/// explicit persistence discriminators while the runtime API stays
/// provider-neutral through <c>ProviderKey</c> and capability metadata.
/// </summary>
public enum KycProvider
{
    /// <summary>In-house manual admin review (the default, no external secrets).</summary>
    MANUAL,

    /// <summary>External automated provider (config-gated; stub until provisioned).</summary>
    VERIFF,

    /// <summary>Operator-configured generic hosted-provider adapter.</summary>
    GENERIC_HOSTED,

    /// <summary>Identomat hosted identity verification + AML screening.</summary>
    IDENTOMAT,

    /// <summary>Unknown or invalid provider configuration; never persisted.</summary>
    UNAVAILABLE
}

/// <summary>
/// Non-terminal/terminal classification of a hosted provider outcome. A provider
/// verdict that is not <see cref="Terminal"/> must never satisfy a value gate.
/// </summary>
public enum KycOutcomeFinality
{
    /// <summary>The provider is still working (queued, in progress, manual check).</summary>
    NonTerminal,

    /// <summary>The provider reached a decision that may be persisted as terminal.</summary>
    Terminal
}

/// <summary>
/// AZOA-derived AML/sanctions screening verdict. Identomat publishes no
/// APPROVED/REJECTED enum on the screening surface; this is entirely our
/// decision, derived from <c>score</c> plus the open <c>topics</c> string set.
/// </summary>
public enum KycScreeningDecision
{
    /// <summary>No hits at or above the queried score threshold.</summary>
    NO_HIT,

    /// <summary>Hits exist but do not meet the auto-block bar; a human must decide.</summary>
    MANUAL_REVIEW,

    /// <summary>A hit meeting the operator's auto-block bar (score and/or topic).</summary>
    HIT
}

/// <summary>Lifecycle of an AZOA-owned ongoing-monitoring registration.</summary>
public enum KycMonitorState
{
    /// <summary>Registered with the provider and being polled.</summary>
    ACTIVE,

    /// <summary>Unregistered from the provider; retained for audit.</summary>
    REMOVED
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
