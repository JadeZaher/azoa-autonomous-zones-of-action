// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// Per-operation one-human-one-claim gate (D3/D23). An operation opts in by
/// declaring a <see cref="PersonUniquenessRequirement"/>; absence of a
/// requirement means no check runs at all.
///
/// Every refusal is the SAME two-valued answer carrying the SAME sentence:
/// <c>Success(KycEligibilityDecision.Ineligible(cause))</c>. Unavailability is a
/// denial, not an outage signal — a 503 that appears only for already-claimed
/// people is the leak wearing a hat. See <c>Services/Kyc/AGENTS.md</c>
/// §person-uniqueness.
///
/// FROZEN CONTRACT — a sibling lane codes against these signatures verbatim.
/// </summary>
public interface IPersonUniquenessGate
{
    /// <summary>
    /// Atomically claims the operation for the person behind <paramref name="userId"/>.
    /// Idempotent: a re-claim by the SAME user in the SAME scope succeeds.
    /// NEVER returns IsError for a refusal.
    ///
    /// D39/D40 — <b>the claim can BE the decision.</b> When the requirement carries a
    /// <see cref="PersonUniquenessRequirement.Decision"/>, the same single statement
    /// that decides the claim also writes that decision onto the one claim row, and a
    /// re-claim by the same holder with a different decision REPLACES it in place.
    /// One row per person means there is no "which ballot is latest" question and so
    /// no ordering to attack. The decision is refused — and the whole call answers
    /// Ineligible — once <see cref="PersonUniquenessRequirement.DecisionClosesAt"/>
    /// has passed, enforced here AND inside the write, never only in the caller.
    /// The signature is unchanged on purpose: the decision rides on the requirement
    /// so no implementor of this frozen contract has to grow a method.
    /// </summary>
    Task<AZOAResult<KycEligibilityDecision>> TryClaimAsync(
        PersonUniquenessRequirement requirement,
        Guid userId,
        Guid? tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Read-only preview; writes nothing. Same two-valued answer, same single
    /// message.
    /// </summary>
    Task<AZOAResult<KycEligibilityDecision>> EvaluateAsync(
        PersonUniquenessRequirement requirement,
        Guid userId,
        Guid? tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Compensation: releases a claim whose side effect failed. Sets
    /// <c>released_at</c>; a released claim no longer blocks. Returns
    /// <c>Success(false)</c> when nothing was held.
    /// </summary>
    Task<AZOAResult<bool>> ReleaseAsync(
        PersonUniquenessRequirement requirement,
        Guid userId,
        Guid? tenantId,
        CancellationToken ct = default);
}
