// SPDX-License-Identifier: UNLICENSED

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence boundary for the opt-in <c>holon_type_registry</c> (final-hardening-cutover
/// F5). Operates on the hand-authored <see cref="HolonType"/> POCO. The AssetType name is
/// the natural key: <see cref="GetByAssetTypeAsync"/> is the hot path consulted by
/// <c>HolonManager</c> on every holon create/update to decide whether the type is
/// constrained.
/// </summary>
/// <remarks>See <c>Providers/Stores/Surreal/AGENTS.md</c> §holon-type-registry.</remarks>
public interface IHolonTypeRegistryStore
{
    /// <summary>Every registered type, most recent first.</summary>
    Task<AZOAResult<IEnumerable<HolonType>>> ListAsync(CancellationToken ct = default);

    /// <summary>The registration for <paramref name="assetType"/>, or a not-found result.</summary>
    Task<AZOAResult<HolonType>> GetByAssetTypeAsync(string assetType, CancellationToken ct = default);

    /// <summary>Creates or replaces the registration keyed by its AssetType.</summary>
    Task<AZOAResult<HolonType>> UpsertAsync(HolonType type, CancellationToken ct = default);

    /// <summary>Removes the registration for <paramref name="assetType"/> (hard delete).</summary>
    Task<AZOAResult<bool>> DeleteAsync(string assetType, CancellationToken ct = default);
}
