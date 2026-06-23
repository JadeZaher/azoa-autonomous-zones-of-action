using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

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

    Task<AZOAResult<DappSeries>> CreateAsync(Guid avatarId, DappSeriesCreateModel model, CancellationToken ct = default);

    Task<AZOAResult<DappSeries>> GetAsync(Guid seriesId, Guid avatarId, CancellationToken ct = default);

    Task<AZOAResult<IEnumerable<DappSeries>>> ListAsync(Guid avatarId, DappSeries.StatusKind? status = null, CancellationToken ct = default);

    Task<AZOAResult<DappSeries>> UpdateAsync(Guid seriesId, Guid avatarId, DappSeriesUpdateModel model, CancellationToken ct = default);

    Task<AZOAResult<bool>> DeleteAsync(Guid seriesId, Guid avatarId, CancellationToken ct = default);

    // ── Quest Management within Series ───────────────────────────────────────

    Task<AZOAResult<DappSeriesQuest>> AddQuestAsync(Guid seriesId, Guid avatarId, DappSeriesAddQuestModel model, CancellationToken ct = default);

    Task<AZOAResult<bool>> RemoveQuestAsync(Guid seriesId, Guid avatarId, Guid questId, CancellationToken ct = default);

    Task<AZOAResult<DappSeriesQuest>> ReorderQuestAsync(Guid seriesId, Guid avatarId, Guid questId, int newOrder, CancellationToken ct = default);

    Task<AZOAResult<DappSeriesQuest>> UpdateMappingsAsync(Guid seriesId, Guid avatarId, Guid questId, string? inputMappings, CancellationToken ct = default);

    Task<AZOAResult<IEnumerable<DappSeriesQuest>>> ListQuestsAsync(Guid seriesId, Guid avatarId, CancellationToken ct = default);

    // ── Composition ──────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the 5 composition rules from
    /// <c>dapp-composition/spec.md §Composition Validation Rules</c> and, if
    /// they all pass, produces and persists a <see cref="DappManifest"/> on
    /// <c>dapp_series.manifest</c>. Status transitions Draft -> Building.
    /// </summary>
    Task<AZOAResult<DappManifest>> ComposeAsync(Guid seriesId, Guid avatarId, CancellationToken ct = default);

    /// <summary>
    /// Reads the validation report without persisting a manifest. Useful for
    /// pre-flight UI checks.
    /// </summary>
    Task<AZOAResult<CompositionValidationResult>> ValidateAsync(Guid seriesId, Guid avatarId, CancellationToken ct = default);

    // ── Generation & Deployment (delegate to ISTARManager) ───────────────────

    /// <summary>
    /// Idempotency-friendly: re-running on a series that already has a
    /// <c>star_odk_id</c> updates the existing record rather than orphaning it.
    /// </summary>
    Task<AZOAResult<ISTARODK>> GenerateAsync(Guid seriesId, Guid avatarId, CancellationToken ct = default);

    Task<AZOAResult<ISTARODK>> DeployAsync(Guid seriesId, Guid avatarId, string? targetOverride = null, CancellationToken ct = default);
}
