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

    /// <summary>
    /// Atomically reserves an NFT still owned by the expected source for one
    /// settlement and target. Replaying the same reservation is idempotent and
    /// preserves its original timestamp; a finalized settlement key cannot be
    /// reused for a later transfer of the holon.
    /// </summary>
    /// <param name="holonId">NFT holon record id.</param>
    /// <param name="sourceAvatarId">Expected current owner.</param>
    /// <param name="targetAvatarId">Owner to install during finalization.</param>
    /// <param name="settlementKey">Stable idempotency key for the settlement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A non-error result whose value is <see langword="true"/> when reserved or
    /// already finalized by this settlement, and <see langword="false"/> when a
    /// competing reservation/owner wins. Invalid input is an error result;
    /// unexpected persistence failures bubble to the host boundary.
    /// </returns>
    Task<AZOAResult<bool>> TryReserveNftTransferAsync(
        Guid holonId,
        Guid sourceAvatarId,
        Guid targetAvatarId,
        string settlementKey,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically moves ownership only when the matching reservation, source,
    /// target, and settlement key are still present.
    /// </summary>
    /// <param name="holonId">NFT holon record id.</param>
    /// <param name="sourceAvatarId">Owner that placed the reservation.</param>
    /// <param name="targetAvatarId">Reserved destination owner.</param>
    /// <param name="settlementKey">Reservation/idempotency key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A non-error result whose value is <see langword="true"/> when finalized
    /// or already finalized by this settlement, and <see langword="false"/> on
    /// a state conflict. Invalid input is an error result; unexpected persistence
    /// failures bubble to the host boundary.
    /// </returns>
    Task<AZOAResult<bool>> FinalizeReservedNftTransferAsync(
        Guid holonId,
        Guid sourceAvatarId,
        Guid targetAvatarId,
        string settlementKey,
        CancellationToken ct = default);

    /// <summary>
    /// Releases only the matching source-owned reservation; replay after a
    /// successful release is idempotent.
    /// </summary>
    /// <param name="holonId">NFT holon record id.</param>
    /// <param name="sourceAvatarId">Owner that placed the reservation.</param>
    /// <param name="settlementKey">Reservation/idempotency key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A non-error result whose value is <see langword="true"/> when released or
    /// already released for the source, and <see langword="false"/> on a state
    /// conflict. Invalid input is an error result; unexpected persistence failures
    /// bubble to the host boundary.
    /// </returns>
    Task<AZOAResult<bool>> ReleaseNftTransferReservationAsync(
        Guid holonId,
        Guid sourceAvatarId,
        string settlementKey,
        CancellationToken ct = default);
}
