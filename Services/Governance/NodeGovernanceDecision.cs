// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Services.Governance;

/// <summary>
/// What node governance decided, in the only vocabulary a caller can reach.
/// </summary>
/// <remarks>
/// <para>
/// OI-30 was first closed by scrubbing the guard's sentence at ONE door
/// (<c>AllocationManager</c>), then at a second (<c>ReserveExchangeManager</c>), while five
/// siblings still returned "Node governance disallows ... on chain 'Algorand'. Allowed chains:
/// ..." verbatim: <c>FungibleTokenManager</c>, <c>NftManager</c> mint/transfer/burn, and
/// <c>HolonManager</c>. Guarding the doors one at a time is exactly how OI-20/21 were falsely
/// closed in L0.
/// </para>
/// <para>
/// So the refusal is scrubbed AT THE COUNTER instead. Both factories below run the operator
/// wording through <see cref="ConsumerSafeErrors"/> before the value exists, so
/// <see cref="ConsumerMessage"/> is consumer-safe BY CONSTRUCTION and there is no SEPARATE
/// member holding the raw sentence for a door to return by mistake.
/// </para>
/// <para>
/// Precisely: the scrub is a REPLACEMENT, not a deletion.
/// <see cref="RefusePolicy(string)"/> keeps the guard's own sentence verbatim when it carries no
/// rail detail - "Node governance requires an asset type ..." is the guard telling a tenant
/// developer what to send, and replacing it with a generic refusal would be a loss, not a
/// safeguard - and replaces it wholesale when it does. So the operator wording CAN reach a
/// caller; what cannot reach a caller is rail detail, which is the property
/// <c>NodeGovernanceGuardTests.RefusePolicy_NeverLetsRailDetailThroughEitherBranch</c> pins by
/// running <see cref="ConsumerSafeErrors.LeaksRailDetail"/> over the constructed value rather
/// than over the input. <see cref="Unavailable(string)"/> is the stricter one: a dependency's own
/// prose is mapped onto a compile-time constant unconditionally.
/// </para>
/// <para>
/// Changing the guard's return type from <c>AZOAResult&lt;bool&gt;</c> to this is what made the
/// compiler walk every existing door rather than trusting a reviewer to find them.
/// </para>
/// </remarks>
public readonly record struct NodeGovernanceDecision
{
    private NodeGovernanceDecision(bool isAllowed, string consumerMessage, string? code)
    {
        IsAllowed = isAllowed;
        ConsumerMessage = consumerMessage;
        Code = code;
    }

    /// <summary>The request may proceed.</summary>
    public bool IsAllowed { get; }

    /// <summary>The request was refused. The reason is <see cref="ConsumerMessage"/>.</summary>
    public bool IsRefused => !IsAllowed;

    /// <summary>
    /// The ONLY sentence this decision may put in front of a member, scrubbed at construction.
    /// Empty when the request was allowed.
    /// </summary>
    public string ConsumerMessage { get; }

    /// <summary>The stable wire code for the refusal; null when the request was allowed.</summary>
    public string? Code { get; }

    /// <summary>The request may proceed. Carries no message, because there is nothing to say.</summary>
    public static NodeGovernanceDecision Allowed { get; } = new(true, string.Empty, null);

    /// <summary>
    /// A POLICY refusal: this node will never run this request as asked. The guard writes this
    /// sentence itself, so wording that carries no rail detail ("Node governance requires an
    /// asset type ...") is kept as written and wording that does is replaced. The invariant is
    /// the OUTPUT, never the input: whichever branch runs, what comes back does not leak.
    /// </summary>
    public static NodeGovernanceDecision RefusePolicy(string operatorDetail)
    {
        var redacted = ConsumerSafeErrors.Redact(operatorDetail);
        return new NodeGovernanceDecision(
            false,
            redacted?.Message ?? operatorDetail,
            ConsumerSafeErrorCodes.NotPermitted);
    }

    /// <summary>
    /// The policy could not be READ - a dependency failure, not a policy one. The detail here is
    /// a store's or a provider's own prose, so it is mapped UNCONDITIONALLY onto a compile-time
    /// constant rather than only when it happens to trip the leak test: what an unknown
    /// dependency writes into an error string is not something this node gets to vouch for.
    /// </summary>
    public static NodeGovernanceDecision Unavailable(string operatorDetail)
        => new(
            false,
            ConsumerSafeErrors.Map(operatorDetail).Message,
            ConsumerSafeErrorCodes.RailUnavailable);
}
