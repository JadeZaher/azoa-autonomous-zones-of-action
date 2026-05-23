using OASIS.WebAPI.Generated.SurrealDb;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence boundary for the <c>dapp-composition</c> aggregate. Operates
/// on the source-gen'd <see cref="DappSeries"/> + <see cref="DappSeriesQuest"/>
/// POCOs from <c>OASIS.WebAPI.Generated.SurrealDb</c> -- there are no
/// hand-written models for this aggregate, per the user directive to
/// generate POCOs from the surreal package for greenfield entities.
/// </summary>
public interface IDappSeriesStore
{
    Task<OASISResult<DappSeries>> GetSeriesAsync(Guid id, CancellationToken ct = default);

    Task<OASISResult<IEnumerable<DappSeries>>> GetSeriesByAvatarAsync(Guid avatarId, CancellationToken ct = default);

    Task<OASISResult<DappSeries>> UpsertSeriesAsync(DappSeries series, CancellationToken ct = default);

    Task<OASISResult<bool>> DeleteSeriesAsync(Guid id, CancellationToken ct = default);

    Task<OASISResult<IEnumerable<DappSeriesQuest>>> GetQuestsBySeriesAsync(Guid seriesId, CancellationToken ct = default);

    Task<OASISResult<DappSeriesQuest>> UpsertSeriesQuestAsync(DappSeriesQuest entry, CancellationToken ct = default);

    Task<OASISResult<bool>> DeleteSeriesQuestAsync(Guid seriesId, Guid questId, CancellationToken ct = default);
}
