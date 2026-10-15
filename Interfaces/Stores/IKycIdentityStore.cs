// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// NODE-OPERATOR-ONLY persistence for the person-to-user binding. No consumer
/// path may reach this store; see <c>Persistence/SurrealDb/Models/AGENTS.md</c>
/// §kyc-identity-binding.
/// </summary>
public interface IKycIdentityStore
{
    /// <summary>
    /// Binds a user to a person key inside a scope, creating the row when absent
    /// and appending the user when present. Returns the resulting binding.
    /// </summary>
    Task<AZOAResult<KycIdentity>> BindUserAsync(
        KycIdentity candidate,
        CancellationToken ct = default);

    /// <summary>Looks a binding up by uniqueness scope and person key.</summary>
    Task<AZOAResult<KycIdentity>> GetByPersonKeyAsync(
        string tenantScope,
        string personKey,
        CancellationToken ct = default);

    /// <summary>
    /// Reverse lookup: the binding an ACTING USER belongs to inside a scope, or a
    /// null result when they have none. This is what lets a per-operation gate turn
    /// an authenticated user into a person key at all; without it the store could
    /// only go person key -> row.
    /// </summary>
    Task<AZOAResult<KycIdentity?>> GetByUserAsync(
        string tenantScope,
        string userId,
        CancellationToken ct = default);

    /// <summary>
    /// Node-operator-only cross-tenant resolution. Never reachable from a consumer
    /// path: this is the explicit per-operation global-scope opt-in of D23.
    /// Support/audit only — the per-operation uniqueness gate does NOT call it,
    /// because it needs a claim ledger, not a person -> users fan-out.
    /// </summary>
    Task<AZOAResult<IReadOnlyList<KycIdentity>>> GetByGlobalPersonKeyAsync(
        string globalPersonKey,
        CancellationToken ct = default);

    /// <summary>Bindings whose retention window has elapsed and are not yet erased.</summary>
    Task<AZOAResult<IReadOnlyList<KycIdentity>>> GetErasureDueAsync(
        DateTimeOffset asOf,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Tombstones the four retained personal fields while PRESERVING both person
    /// keys, the user links, and the audit timestamps, so erasure and KYC
    /// record-keeping both hold (D25).
    /// </summary>
    Task<AZOAResult<KycIdentity>> EraseAsync(
        string recordId,
        string reason,
        DateTimeOffset erasedAt,
        CancellationToken ct = default);
}
