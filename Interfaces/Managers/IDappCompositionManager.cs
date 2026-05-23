using OASIS.WebAPI.Generated.SurrealDb;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces.Managers;

/// <summary>
/// Manager for the <c>dapp-composition</c> aggregate: composes an ordered
/// series of quest DAGs into a deployable dApp contract via STAR generation.
/// Operates on the source-gen'd <see cref="DappSeries"/> +
/// <see cref="DappSeriesQuest"/> POCOs throughout -- no hand-written
/// persistence types.
/// </summary>
public interface IDappCompositionManager
{
    // ── Series CRUD ──────────────────────────────────────────────────────────

    Task<OASISResult<DappSeries>> CreateAsync(Guid avatarId, DappSeriesCreateModel model, CancellationToken ct = default);

    Task<OASISResult<DappSeries>> GetAsync(Guid seriesId, Guid avatarId, CancellationToken ct = default);

    Task<OASISResult<IEnumerable<DappSeries>>> ListAsync(Guid avatarId, DappSeries.StatusKind? status = null, CancellationToken ct = default);

    Task<OASISResult<DappSeries>> UpdateAsync(Guid seriesId, Guid avatarId, DappSeriesUpdateModel model, CancellationToken ct = default);

    Task<OASISResult<bool>> DeleteAsync(Guid seriesId, Guid avatarId, CancellationToken ct = default);

    // ── Quest Management within Series ───────────────────────────────────────

    Task<OASISResult<DappSeriesQuest>> AddQuestAsync(Guid seriesId, Guid avatarId, DappSeriesAddQuestModel model, CancellationToken ct = default);

    Task<OASISResult<bool>> RemoveQuestAsync(Guid seriesId, Guid avatarId, Guid questId, CancellationToken ct = default);

    Task<OASISResult<DappSeriesQuest>> ReorderQuestAsync(Guid seriesId, Guid avatarId, Guid questId, int newOrder, CancellationToken ct = default);

    Task<OASISResult<DappSeriesQuest>> UpdateMappingsAsync(Guid seriesId, Guid avatarId, Guid questId, string? inputMappings, CancellationToken ct = default);

    Task<OASISResult<IEnumerable<DappSeriesQuest>>> ListQuestsAsync(Guid seriesId, Guid avatarId, CancellationToken ct = default);

    // ── Composition ──────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the 5 composition rules from
    /// <c>dapp-composition/spec.md §Composition Validation Rules</c> and, if
    /// they all pass, produces and persists a <see cref="DappManifest"/> on
    /// <c>dapp_series.manifest</c>. Status transitions Draft -> Building.
    /// </summary>
    Task<OASISResult<DappManifest>> ComposeAsync(Guid seriesId, Guid avatarId, CancellationToken ct = default);

    /// <summary>
    /// Reads the validation report without persisting a manifest. Useful for
    /// pre-flight UI checks.
    /// </summary>
    Task<OASISResult<CompositionValidationResult>> ValidateAsync(Guid seriesId, Guid avatarId, CancellationToken ct = default);

    // ── Generation & Deployment (delegate to ISTARManager) ───────────────────

    /// <summary>
    /// Idempotency-friendly: re-running on a series that already has a
    /// <c>star_odk_id</c> updates the existing record rather than orphaning it.
    /// </summary>
    Task<OASISResult<ISTARODK>> GenerateAsync(Guid seriesId, Guid avatarId, CancellationToken ct = default);

    Task<OASISResult<ISTARODK>> DeployAsync(Guid seriesId, Guid avatarId, string? targetOverride = null, CancellationToken ct = default);
}
