using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence boundary for the STARODK ecosystem tree (star-odk-ecosystem-tree
/// / final-hardening-cutover D2). Operates on the decorated POCOs
/// <see cref="Ecosystem"/> + <see cref="EcosystemNode"/> — no hand-written
/// domain model at the storage boundary; the manager maps to the Guid-typed
/// <c>Models.Ecosystem</c> shapes for the API surface.
/// </summary>
public interface IEcosystemStore
{
    /// <summary>Loads the ecosystem owned by <paramref name="starOdkId"/> (one per STARODK), or null.</summary>
    Task<AZOAResult<Ecosystem>> GetByStarOdkAsync(Guid starOdkId, CancellationToken ct = default);

    /// <summary>Loads an ecosystem by its own id.</summary>
    Task<AZOAResult<Ecosystem>> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Inserts or updates the ecosystem root record.</summary>
    Task<AZOAResult<Ecosystem>> UpsertAsync(Ecosystem ecosystem, CancellationToken ct = default);

    /// <summary>Loads every node in an ecosystem tree (unordered; the manager assembles the hierarchy).</summary>
    Task<AZOAResult<IEnumerable<EcosystemNode>>> GetNodesAsync(Guid ecosystemId, CancellationToken ct = default);

    /// <summary>Inserts or updates a single ecosystem tree node.</summary>
    Task<AZOAResult<EcosystemNode>> UpsertNodeAsync(EcosystemNode node, CancellationToken ct = default);

    /// <summary>Deletes an ecosystem and all its nodes.</summary>
    Task<AZOAResult<bool>> DeleteAsync(Guid ecosystemId, CancellationToken ct = default);
}
