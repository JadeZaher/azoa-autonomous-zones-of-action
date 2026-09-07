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
    /// </summary>
    Task<AZOAResult<PersonOperationClaim>> TryClaimAsync(
        PersonOperationClaim candidate,
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
