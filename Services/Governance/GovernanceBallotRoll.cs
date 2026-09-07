// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Kyc;

namespace AZOA.WebAPI.Services.Governance;

/// <summary>The only thing a tally is allowed to learn: a total and per-option counts.</summary>
/// <remarks>
/// A type, not a bag of holons, so that "counts leave, voters do not" is enforced by
/// the signature rather than by the caller remembering. See
/// <c>Services/Governance/AGENTS.md</c> §consumer-governance.
/// </remarks>
public sealed record GovernanceBallotCount(int TotalVotes, SortedDictionary<string, int> CountsByOption);

/// <summary>
/// Counts the ballots on a proposal that are BACKED BY A LIVE UNIQUENESS CLAIM
/// (D20/D23). This is the join that makes "one member, one vote" real.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the claim ledger and not the ballots.</b> A ballot is an ordinary child
/// Holon, and <c>POST /api/holon</c> will happily write one: any verified user can
/// mint five hundred <c>GovernanceVote</c> children under a proposal without ever
/// touching <c>GovernanceVoteNodeHandler</c>, so a tally that counts child holons
/// counts forgeries. <c>person_operation_claim</c> is node-operator-only and
/// unreachable from every consumer path, so it is the only unforgeable evidence
/// that a ballot was cast by a distinct verified human. A ballot counts iff a live
/// claim on this proposal's uniqueness domain is held BY THAT BALLOT'S OWNER, and
/// each claim contributes AT MOST ONE ballot.
/// </para>
/// <para>
/// <b>Why the store and not <c>IHolonManager</c>.</b> Ballots are private and each is
/// owned by its own voter, so the consumer-scoped owner-or-public read returns only
/// the runner's own ballot and a tally built on it certifies a count of one. The
/// alternative — publishing ballots — would attach <c>Holon.UserId</c> to every
/// choice and reconstitute the person-to-choice correlation D20 exists to prevent.
/// So the unscoped read lives HERE, in one auditable place, and the only thing that
/// leaves is a <see cref="GovernanceBallotCount"/>.
/// </para>
/// <para>
/// <b>Fail closed, never fail quiet.</b> A store fault is an error result, not a
/// skipped ballot: silently dropping ballots would publish a wrong count under a
/// correct-looking <c>tallyHash</c>.
/// </para>
/// </remarks>
public sealed class GovernanceBallotRoll
{
    private readonly IHolonStore _holons;
    private readonly IKycIdentityStore _identities;
    private readonly IPersonOperationClaimStore _claims;

    public GovernanceBallotRoll(
        IHolonStore holons,
        IKycIdentityStore identities,
        IPersonOperationClaimStore claims)
    {
        _holons = holons;
        _identities = identities;
        _claims = claims;
    }

    /// <summary>
    /// Counts the claim-backed ballots on <paramref name="proposalId"/>. Every
    /// declared option appears, including the ones nobody chose — a count that
    /// omitted a zero would move the tally-hash preimage as votes arrive.
    /// </summary>
    /// <param name="proposalId">The proposal whose child holons are the ballots.</param>
    /// <param name="scope">The uniqueness domain the PROPOSAL declared.</param>
    /// <param name="tenantId">Acting tenant of the tally run; resolves the tenant scope.</param>
    /// <param name="declaredOptions">The proposal's declared option set.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AZOAResult<GovernanceBallotCount>> CountClaimBackedBallotsAsync(
        Guid proposalId,
        PersonUniquenessScope scope,
        Guid? tenantId,
        IReadOnlyList<string> declaredOptions,
        CancellationToken ct = default)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var option in declaredOptions)
            counts[option] = 0;

        // The SAME canonicalization the claim was written under. A key that will not
        // canonicalize cannot match any stored claim, so it is a closed failure
        // rather than a tally of zero.
        var operationKey = PersonUniquenessRequirement.Canonicalize(
            ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor(proposalId));
        if (operationKey is null)
            return AZOAResult<GovernanceBallotCount>.Failure("the proposal's uniqueness key is unusable.");

        // Reuses the ONE canonical scope rule (KycPersonKeyDeriver.ScopeValue) that
        // PersonUniquenessGate claims through, so the tally and the vote ask their
        // question in the same domain by construction. A Tenant-scoped proposal
        // therefore counts the claims of the tally's own tenant and nobody else's.
        var tenantScope = KycPersonKeyDeriver.ScopeValue(tenantId);
        var global = scope == PersonUniquenessScope.Global;
        var uniquenessScope = global ? KycPersonKeyDeriver.GlobalScopeSentinel : tenantScope;

        var children = await _holons.QueryAsync(new HolonQueryRequest { ParentHolonId = proposalId }, ct);
        if (children.IsError)
            return AZOAResult<GovernanceBallotCount>.Failure(
                string.IsNullOrWhiteSpace(children.Message)
                    ? "the proposal's ballots could not be read."
                    : children.Message);

        // Deterministic order so the SAME ballot wins the claim on every re-tally and
        // the published hash is stable: earliest castAt first, id as the tie-break.
        var ballots = (children.Result ?? Array.Empty<IHolon>())
            .Where(child =>
                string.Equals(child.AssetType, ConsumerGovernanceHolonTypes.VoteAssetType, StringComparison.Ordinal)
                && child.UserId.HasValue)
            .OrderBy(CastAtOrMax)
            .ThenBy(child => child.Id)
            .ToList();

        // One lookup pair per distinct voter; a null entry means "holds no claim".
        var backingByVoter = new Dictionary<Guid, string?>();
        var spentClaims = new HashSet<string>(StringComparer.Ordinal);
        var totalVotes = 0;

        foreach (var ballot in ballots)
        {
            var option = ballot.Metadata.GetValueOrDefault(ConsumerGovernanceHolonTypes.VoteMetadata.Option);
            if (option is null || !counts.ContainsKey(option))
                continue;   // a ballot for an undeclared option cannot be cast through the vote node.

            var voterId = ballot.UserId!.Value;
            if (!backingByVoter.TryGetValue(voterId, out var claimId))
            {
                var backing = await ResolveBackingClaimIdAsync(
                    voterId, tenantScope, uniquenessScope, operationKey, global, ct);
                if (backing.IsError)
                    return AZOAResult<GovernanceBallotCount>.Failure(backing.Message);

                claimId = backing.Result;
                backingByVoter[voterId] = claimId;
            }

            if (claimId is null)
                continue;               // forged or unbacked: no live claim is held by this voter.
            if (!spentClaims.Add(claimId))
                continue;               // one human, one counted ballot.

            counts[option]++;
            totalVotes++;
        }

        return AZOAResult<GovernanceBallotCount>.Success(new GovernanceBallotCount(totalVotes, counts));
    }

    /// <summary>
    /// The claim record id backing <paramref name="voterId"/>, or a null RESULT when
    /// nothing backs them. An error result means the question could not be answered
    /// and the whole tally must fail — the caller must never read that as "no claim".
    /// </summary>
    private async Task<AZOAResult<string?>> ResolveBackingClaimIdAsync(
        Guid voterId,
        string tenantScope,
        string uniquenessScope,
        string operationKey,
        bool global,
        CancellationToken ct)
    {
        var userKey = voterId.ToString("N");

        // Same reverse lookup the gate claims through: acting user -> their identity
        // row in the TENANT scope (never the global sentinel), then the
        // scope-appropriate person key.
        var identity = await _identities.GetByUserAsync(tenantScope, userKey, ct);
        if (identity.IsError)
            return AZOAResult<string?>.Failure("the ballots' identity bindings could not be read.");
        if (identity.Result is null)
            return AZOAResult<string?>.Success(null, "No identity binding.");

        var personKey = global ? identity.Result.GlobalPersonKey : identity.Result.PersonKey;
        if (string.IsNullOrWhiteSpace(personKey))
            return AZOAResult<string?>.Success(null, "No usable person key.");

        var claim = await _claims.GetAsync(uniquenessScope, operationKey, personKey, ct);
        if (claim.IsError)
            return AZOAResult<string?>.Failure("the uniqueness claim ledger could not be read.");

        var held = claim.Result;
        if (held is null || held.ReleasedAt is not null)
            return AZOAResult<string?>.Success(null, "No live claim.");

        // A released-and-reclaimed row can be held by a DIFFERENT user of the same
        // human; only the holder's ballot counts, so the other user's ballot is
        // unbacked even though the human is claimed.
        if (!string.Equals(held.UserId, userKey, StringComparison.Ordinal))
            return AZOAResult<string?>.Success(null, "The claim is held by another user.");

        return AZOAResult<string?>.Success(held.Id, "Claim-backed.");
    }

    /// <summary>
    /// The ballot's own <c>castAt</c>, or <see cref="DateTimeOffset.MaxValue"/> when it
    /// is missing or unparsable, so a malformed ballot sorts last and never displaces
    /// a well-formed one from its claim.
    /// </summary>
    private static DateTimeOffset CastAtOrMax(IHolon ballot)
    {
        var raw = ballot.Metadata.GetValueOrDefault(ConsumerGovernanceHolonTypes.VoteMetadata.CastAt);
        return !string.IsNullOrWhiteSpace(raw)
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var castAt)
                ? castAt
                : DateTimeOffset.MaxValue;
    }
}
