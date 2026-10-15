using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence boundary for the <c>dapp-composition</c> aggregate. Operates
/// on the source-gen'd <see cref="DappSeries"/> + <see cref="DappSeriesAction"/>
/// POCOs from <c>AZOA.WebAPI.Persistence.SurrealDb.Models</c> -- there are no
/// hand-written models for this aggregate, per the user directive to
/// generate POCOs from the surreal package for greenfield entities.
/// </summary>
public interface IDappSeriesStore
{
    Task<AZOAResult<DappSeries>> GetSeriesAsync(Guid id, CancellationToken ct = default);

    Task<AZOAResult<IEnumerable<DappSeries>>> GetSeriesByUserAsync(Guid userId, CancellationToken ct = default);

    Task<AZOAResult<DappSeries>> UpsertSeriesAsync(DappSeries series, CancellationToken ct = default);

    Task<AZOAResult<bool>> DeleteSeriesAsync(Guid id, CancellationToken ct = default);

    Task<AZOAResult<IEnumerable<DappSeriesAction>>> GetActionsBySeriesAsync(Guid seriesId, CancellationToken ct = default);

    Task<AZOAResult<DappSeriesAction>> UpsertSeriesActionAsync(DappSeriesAction entry, CancellationToken ct = default);

    Task<AZOAResult<bool>> DeleteSeriesActionAsync(Guid seriesId, Guid actionId, CancellationToken ct = default);
}
