// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Governance;

/// <summary>
/// The only thing a tally is allowed to learn: a total, per-option counts, and which
/// table they were read out of.
/// </summary>
/// <remarks>
/// <para>
/// A type rather than a bag of rows, so "counts leave, voters do not" is enforced by
/// the signature instead of by the caller remembering.
/// </para>
/// <para>
/// <see cref="SourceTable"/> is not decoration. The tally handler refuses to publish
/// anything whose source table is not <c>person_operation_claim</c>, so a count that
/// came from somewhere else cannot be dressed up as a result. See
/// <c>Services/Governance/AGENTS.md</c> §consumer-governance.
/// </para>
/// </remarks>
public sealed record GovernanceVoteOfRecordCount(
    int TotalVotes,
    SortedDictionary<string, int> CountsByOption,
    string SourceTable,
    int LiveClaimsRead);

/// <summary>
/// Counts a proposal's decisions straight out of the per-operation claim ledger
/// (D39). This class is the whole tally: there is no holon to join to and therefore
/// no join anyone can drop.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the ledger and nothing else.</b> The shipped tally counted child holons,
/// and <c>POST /api/holon</c> writes those: one verified user could mint five hundred
/// <c>GovernanceVote</c> children without ever reaching the vote node. Joining those
/// holons to the claim ledger narrowed the hole without closing it — the join still
/// let an attacker-written holon decide WHICH option a claim was spent on, because
/// both the option and the ordering key came out of holon metadata the forger owns.
/// D39 removes the holon from the answer entirely. <c>person_operation_claim</c> is
/// node-operator-only, holds exactly one row per person per operation, is written
/// inside a single single-winner statement, and now carries the chosen option, so a
/// forged holon counts for nothing BY CONSTRUCTION rather than by a check somebody
/// has to remember.
/// </para>
/// <para>
/// <b>One row, one vote, no ordering.</b> Because a person has exactly one row, a
/// re-vote is an in-place update of that row's option. There is no "which ballot is
/// latest" question to answer, so the ordering attack the shipped roll was vulnerable
/// to does not exist here rather than being defended against.
/// </para>
/// <para>
/// <b>Fail closed, never fail quiet.</b> A store fault is an error result, not a
/// skipped voter: silently dropping decisions publishes a wrong count under a
/// correct-looking <c>tallyHash</c>.
/// </para>
/// </remarks>
public sealed class GovernanceClaimLedgerTally
{
    private readonly IPersonOperationClaimStore _claims;

    public GovernanceClaimLedgerTally(IPersonOperationClaimStore claims)
    {
        _claims = claims;
    }

    /// <summary>
    /// Counts the live decisions recorded in one uniqueness domain. Every declared
    /// option comes back, including the ones nobody chose — a count that silently
    /// omitted a zero would move the tally-hash preimage as votes arrive.
    /// </summary>
    /// <param name="uniquenessScopeValue">The domain the proposal's ELECTORATE claims in.</param>
    /// <param name="proposalId">The proposal, used only to derive its operation key.</param>
    /// <param name="declaredOptions">The proposal's declared option set.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AZOAResult<GovernanceVoteOfRecordCount>> CountVotesOfRecordAsync(
        string uniquenessScopeValue,
        Guid proposalId,
        IReadOnlyList<string> declaredOptions,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(declaredOptions);

        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var option in declaredOptions)
            counts[option] = 0;

        if (string.IsNullOrWhiteSpace(uniquenessScopeValue))
            return AZOAResult<GovernanceVoteOfRecordCount>.Failure(
                "the proposal declares no usable electorate.");

        // The SAME canonicalization the claim was written under. A key that will not
        // canonicalize cannot match any stored claim, so it is a closed failure rather
        // than a published tally of zero.
        var operationKey = PersonUniquenessRequirement.Canonicalize(
            ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor(proposalId));
        if (operationKey is null)
            return AZOAResult<GovernanceVoteOfRecordCount>.Failure("the proposal's uniqueness key is unusable.");

        var rows = await _claims.ListLiveByOperationAsync(uniquenessScopeValue, operationKey, ct);
        if (rows.IsError || rows.Result is null)
            return AZOAResult<GovernanceVoteOfRecordCount>.Failure(
                string.IsNullOrWhiteSpace(rows.Message)
                    ? "the proposal's votes of record could not be read."
                    : rows.Message);

        var totalVotes = 0;
        foreach (var claim in rows.Result)
        {
            // A released claim is not a vote; the store already excludes them, and
            // re-checking here costs nothing and survives a store that forgets.
            if (claim.ReleasedAt is not null || string.IsNullOrWhiteSpace(claim.UserId))
                continue;

            // A claim taken by an operation that records no decision — or one whose
            // option the proposal no longer declares — is not a vote for anything.
            var option = claim.DecisionOption;
            if (option is null || !counts.ContainsKey(option))
                continue;

            counts[option]++;
            totalVotes++;
        }

        return AZOAResult<GovernanceVoteOfRecordCount>.Success(
            new GovernanceVoteOfRecordCount(
                totalVotes,
                counts,
                PersonOperationClaim.SchemaNameConst,
                rows.Result.Count),
            "Counted.");
    }
}
