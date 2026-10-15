// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// NODE-OPERATOR-ONLY persistence for the per-operation Sybil ledger. No consumer
/// path may reach this store; see <c>Persistence/SurrealDb/Models/AGENTS.md</c>
/// §person-operation-claim.
/// </summary>
public interface IPersonOperationClaimStore
{
    /// <summary>
    /// Attempts the claim. The winner is decided INSIDE one statement on the
    /// deterministic record id; the caller decides refusal by comparing the
    /// returned <c>UserId</c> to its own, never by a preceding read (two
    /// concurrent claims both pass a preceding read).
    ///
    /// D39/D40: when the candidate carries a <c>DecisionOption</c>, the SAME
    /// statement writes it in place — so a re-vote is an update of the one row, not
    /// a second ballot — but only while the row's pinned <c>DecisionClosesAt</c> has
    /// not passed. The caller confirms by comparing the returned decision to the one
    /// it sent; a refused decision leaves the previous one untouched.
    /// </summary>
    Task<AZOAResult<PersonOperationClaim>> TryClaimAsync(
        PersonOperationClaim candidate,
        CancellationToken ct = default);

    /// <summary>
    /// D39: every LIVE (unreleased) claim in one uniqueness domain — the vote of
    /// record for an entire operation, and the ONLY source a governance tally reads.
    /// Deliberately keyed by (scope, operation) and nothing else: a caller cannot ask
    /// this store about a person it names, so the read cannot be turned into a
    /// person-key oracle. A store fault is an error result, never an empty list — an
    /// empty list is a published tally of zero.
    /// </summary>
    Task<AZOAResult<IReadOnlyList<PersonOperationClaim>>> ListLiveByOperationAsync(
        string uniquenessScope,
        string operationKey,
        CancellationToken ct = default);

    /// <summary>Read-only lookup of the current holder, or null when unclaimed.</summary>
    Task<AZOAResult<PersonOperationClaim?>> GetAsync(
        string uniquenessScope,
        string operationKey,
        string personKey,
        CancellationToken ct = default);

    /// <summary>
    /// Releases a live claim held by <paramref name="userId"/>. Returns
    /// <c>Success(false)</c> when nothing was held by that user, so a replayed
    /// compensation is idempotent.
    /// </summary>
    Task<AZOAResult<bool>> ReleaseAsync(
        string uniquenessScope,
        string operationKey,
        string personKey,
        string userId,
        DateTimeOffset releasedAt,
        CancellationToken ct = default);
}
