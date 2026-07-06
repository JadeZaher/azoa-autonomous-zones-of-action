using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// Orchestrates the opt-in Holon AssetType registry (final-hardening-cutover F5).
/// Two roles: (1) an operator-facing CRUD surface for the platform vocabulary of
/// asset types, and (2) the <see cref="ValidateAsync"/> hook <c>HolonManager</c>
/// consults on every holon create/update.
/// </summary>
/// <remarks>
/// <b>Opt-in semantics</b>: an AssetType with no active registration is unconstrained
/// (free string — existing holon creation never breaks). Only a registered, active type
/// is validated, and only for the metadata fields it declares required.
/// </remarks>
public interface IHolonTypeRegistryManager
{
    Task<AZOAResult<IEnumerable<HolonType>>> ListAsync(AZOARequest? request = null);

    Task<AZOAResult<HolonType>> GetAsync(string assetType, AZOARequest? request = null);

    Task<AZOAResult<HolonType>> RegisterAsync(HolonTypeRegisterModel model, AZOARequest? request = null);

    /// <summary>Marks a registered type inactive (validation then ignores it) without deleting it.</summary>
    Task<AZOAResult<HolonType>> DeactivateAsync(string assetType, AZOARequest? request = null);

    Task<AZOAResult<bool>> DeleteAsync(string assetType, AZOARequest? request = null);

    /// <summary>
    /// Opt-in validation hook. Returns success when <paramref name="assetType"/> is null/empty,
    /// unregistered, or inactive (all unconstrained). When a registered active type declares
    /// required metadata fields, returns an error naming any that are missing or empty in
    /// <paramref name="metadata"/>.
    /// </summary>
    Task<AZOAResult<bool>> ValidateAsync(
        string? assetType, IReadOnlyDictionary<string, string>? metadata, AZOARequest? request = null);
}
