// SPDX-License-Identifier: UNLICENSED

#nullable enable

using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Models.Kyc;

/// <summary>
/// Operator-side reason a user was refused. NEVER projected to a consumer:
/// distinguishing "already bound elsewhere" from "not verified" tells a caller
/// that their subject exists under a different tenant on this node.
/// </summary>
public enum KycIneligibilityCause
{
    /// <summary>No binding exists for this person key in this scope.</summary>
    NoVerifiedIdentity,

    /// <summary>The person key is already bound to a different user in this scope.</summary>
    PersonAlreadyBoundToAnotherUser,

    /// <summary>The binding exists but its personal fields were erased.</summary>
    IdentityErased,

    /// <summary>
    /// D3: this person already holds the per-operation claim, through this or
    /// another user. Operator-only — telling a consumer would reveal that its
    /// subject acted under a different user or tenant.
    /// </summary>
    PersonAlreadyClaimedOperation,

    /// <summary>
    /// The uniqueness check could not be answered: unconfigured key material, an
    /// unusable operation key, or an unreachable store. Unavailability is a
    /// DENIAL, not an outage signal — a 503 that appears only for already-claimed
    /// people is the leak wearing a hat.
    /// </summary>
    UniquenessUnavailable
}

/// <summary>
/// Two-valued eligibility answer. <see cref="Cause"/> is operator-only and is
/// suppressed from serialization so it cannot leak through a response body that
/// happens to serialize the whole object.
/// </summary>
public sealed class KycEligibilityDecision
{
    /// <summary>Whether the user may proceed.</summary>
    public bool IsEligible { get; init; }

    /// <summary>The single message any consumer may see when refused.</summary>
    public string? Message { get; init; }

    /// <summary>Operator-only discriminator; never serialized.</summary>
    [JsonIgnore]
    public KycIneligibilityCause? Cause { get; init; }

    public static KycEligibilityDecision Eligible() => new() { IsEligible = true };

    public static KycEligibilityDecision Ineligible(KycIneligibilityCause cause) => new()
    {
        IsEligible = false,
        Cause = cause,
        Message = "This user is not eligible for this operation.",
    };
}
