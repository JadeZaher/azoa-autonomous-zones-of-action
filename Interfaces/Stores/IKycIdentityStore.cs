// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// NODE-OPERATOR-ONLY persistence for the person-to-avatar binding. No consumer
/// path may reach this store; see <c>Persistence/SurrealDb/Models/AGENTS.md</c>
/// §kyc-identity-binding.
/// </summary>
public interface IKycIdentityStore
{
    /// <summary>
    /// Binds an avatar to a person key inside a scope, creating the row when absent
    /// and appending the avatar when present. Returns the resulting binding.
    /// </summary>
    Task<AZOAResult<KycIdentity>> BindAvatarAsync(
        KycIdentity candidate,
        CancellationToken ct = default);

    /// <summary>Looks a binding up by uniqueness scope and person key.</summary>
    Task<AZOAResult<KycIdentity>> GetByPersonKeyAsync(
        string tenantScope,
        string personKey,
        CancellationToken ct = default);

    /// <summary>
    /// Node-operator-only cross-tenant resolution. Never reachable from a consumer
    /// path: this is the explicit per-operation global-scope opt-in of D23.
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
    /// keys, the avatar links, and the audit timestamps, so erasure and KYC
    /// record-keeping both hold (D25).
    /// </summary>
    Task<AZOAResult<KycIdentity>> EraseAsync(
        string recordId,
        string reason,
        DateTimeOffset erasedAt,
        CancellationToken ct = default);
}
