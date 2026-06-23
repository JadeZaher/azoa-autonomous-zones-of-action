using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Persistence boundary for <see cref="IHolon"/> aggregates.</summary>
public interface IHolonStore
{
    /// <summary>Loads a single holon by id.</summary>
    Task<AZOAResult<IHolon>> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Loads holons matching <paramref name="query"/>; a null query returns all
    /// holons. Covers name/parent/child/graph filters carried by the request.
    /// </summary>
    Task<AZOAResult<IEnumerable<IHolon>>> QueryAsync(HolonQueryRequest? query = null, CancellationToken ct = default);

    /// <summary>Inserts or updates a holon.</summary>
    Task<AZOAResult<IHolon>> UpsertAsync(IHolon holon, CancellationToken ct = default);

    /// <summary>Deletes a holon by id.</summary>
    Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default);
}
