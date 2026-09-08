// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Services.Fiat.Reversal;

/// <summary>
/// Operator configuration for the reversal pipeline. Bound from the <c>Fiat:Reversal</c> section;
/// see <c>Services/Fiat/Reversal/AGENTS.md</c>.
/// </summary>
public sealed class ReversalOptions
{
    /// <summary>Configuration section this binds from.</summary>
    public const string SectionName = "Fiat:Reversal";

    /// <summary>Fewest compensation attempts one reversal may consume.</summary>
    public const int MinimumRecoveryAttempts = 1;

    /// <summary>Most compensation attempts one reversal may consume.</summary>
    public const int MaximumRecoveryAttempts = 20;

    /// <summary>
    /// How many times the compensation ladder may be attempted before the reversal is parked for
    /// an operator. It bounds only INDETERMINATE outcomes: a definite "nothing is recoverable"
    /// verdict escalates immediately rather than burning the budget.
    /// </summary>
    public int MaxRecoveryAttempts { get; set; } = 5;

    /// <summary>The policy used for a flow the operator has declared nothing for.</summary>
    public ReversalFlowPolicy Default { get; set; } = new();

    /// <summary>
    /// Per-flow overrides, keyed by the funding intent's purpose (the same allowlisted routing
    /// word the checkout was created with, e.g. <c>funds-top-up</c>).
    /// </summary>
    public Dictionary<string, ReversalFlowPolicy> Flows { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The effective attempt bound, clamped to the range above.</summary>
    public int EffectiveMaxRecoveryAttempts =>
        Math.Clamp(MaxRecoveryAttempts, MinimumRecoveryAttempts, MaximumRecoveryAttempts);

    /// <summary>
    /// The policy that governs one reversal. An unknown or absent flow gets
    /// <see cref="Default"/> rather than the most permissive entry: a flow nobody configured must
    /// behave like the node's stated default, not like whichever policy happens to be first.
    /// </summary>
    public ReversalFlowPolicy PolicyFor(string? flowPurpose)
    {
        if (!string.IsNullOrWhiteSpace(flowPurpose)
            && Flows.TryGetValue(flowPurpose!.Trim(), out var policy)
            && policy is not null)
        {
            return policy;
        }

        return Default ?? new ReversalFlowPolicy();
    }
}

/// <summary>
/// What the node is willing to do to unwind one flow's reversed payment. Per flow because the
/// flows are not alike: taking units back out of an account is proportionate when a sponsor
/// charges back a large top-up and disproportionate when a two-dollar payment is refunded by the
/// node's own support desk.
/// </summary>
public sealed class ReversalFlowPolicy
{
    /// <summary>Rung 1: take the units back out of the payer's own account while they are there.</summary>
    public bool RecoverFromBalance { get; set; } = true;

    /// <summary>Rung 2a: give back an open hold the reversed money had funded.</summary>
    public bool RecoverFromOpenHolds { get; set; } = true;

    /// <summary>Rung 2b: take back the units the reversed money bought.</summary>
    public bool RecoverFromPurchasedUnits { get; set; } = true;

    /// <summary>
    /// Reversals at or below this many minor units are absorbed without touching the payer at
    /// all. Zero (the default) absorbs nothing automatically.
    /// </summary>
    /// <remarks>
    /// The rail charge for a recovery move can exceed a small reversal, and chasing a person for
    /// two dollars costs more than it returns. Making the threshold explicit and operator-set is
    /// the difference between a policy and an accident.
    /// </remarks>
    public long AbsorbAtOrBelowMinorUnits { get; set; }

    /// <summary>
    /// Whether a dispute that has been OPENED but whose money the provider has not taken yet
    /// should already start the compensation ladder.
    /// </summary>
    /// <remarks>
    /// Off by default. A dispute can be resolved in the node's favour, and clawing units back the
    /// moment somebody asks a question punishes them for asking. The account flag below is the
    /// proportionate response at this point: no new value actions while the question is open.
    /// </remarks>
    public bool RecoverOnDisputeOpened { get; set; }

    /// <summary>Whether opening a dispute bars the account from further value actions.</summary>
    public bool FlagAccountOnDisputeOpened { get; set; } = true;

    /// <summary>Whether a reversal the node had to absorb bars the account from further value actions.</summary>
    public bool FlagAccountOnShortfall { get; set; } = true;
}
