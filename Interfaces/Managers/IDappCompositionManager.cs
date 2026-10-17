using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Deployments;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// Manager for the <c>dapp-composition</c> aggregate: composes an ordered
/// series of action DAGs into a deployable dApp contract via action graph bundle generation.
/// Operates on the source-gen'd <see cref="DappSeries"/> +
/// <see cref="DappSeriesAction"/> POCOs throughout -- no hand-written
/// persistence types.
/// </summary>
public interface IDappCompositionManager
{
    // ── Series CRUD ──────────────────────────────────────────────────────────

    Task<AZOAResult<DappSeries>> CreateAsync(Guid userId, DappSeriesCreateModel model, CancellationToken ct = default);

    Task<AZOAResult<DappSeries>> GetAsync(Guid seriesId, Guid userId, CancellationToken ct = default);

    Task<AZOAResult<IEnumerable<DappSeries>>> ListAsync(Guid userId, DappSeries.StatusKind? status = null, CancellationToken ct = default);

    Task<AZOAResult<DappSeries>> UpdateAsync(Guid seriesId, Guid userId, DappSeriesUpdateModel model, CancellationToken ct = default);

    Task<AZOAResult<bool>> DeleteAsync(Guid seriesId, Guid userId, CancellationToken ct = default);

    // ── Action Management within Series ─────────────────────────────────────────────────

    Task<AZOAResult<DappSeriesAction>> AddActionAsync(Guid seriesId, Guid userId, DappSeriesAddActionModel model, CancellationToken ct = default);

    Task<AZOAResult<bool>> RemoveActionAsync(Guid seriesId, Guid userId, Guid actionId, CancellationToken ct = default);

    Task<AZOAResult<DappSeriesAction>> ReorderActionAsync(Guid seriesId, Guid userId, Guid actionId, int newOrder, CancellationToken ct = default);

    Task<AZOAResult<DappSeriesAction>> UpdateMappingsAsync(Guid seriesId, Guid userId, Guid actionId, string? inputMappings, CancellationToken ct = default);

    Task<AZOAResult<IEnumerable<DappSeriesAction>>> ListActionsAsync(Guid seriesId, Guid userId, CancellationToken ct = default);

    // ── Composition ──────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the 5 composition rules from
    /// <c>dapp-composition/spec.md §Composition Validation Rules</c> and, if
    /// they all pass, produces and persists a <see cref="DappManifest"/> on
    /// <c>dapp_series.manifest</c>. Status transitions Draft -> Building.
    /// </summary>
    Task<AZOAResult<DappManifest>> ComposeAsync(Guid seriesId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Reads the validation report without persisting a manifest. Useful for
    /// pre-flight UI checks.
    /// </summary>
    Task<AZOAResult<CompositionValidationResult>> ValidateAsync(Guid seriesId, Guid userId, CancellationToken ct = default);

    // ── Generation & Deployment (delegate to IActionGraphBundleManager) ───────────────────

    /// <summary>
    /// Idempotency-friendly: re-running on a series that already has a
    /// <c>action_graph_bundle_id</c> updates the existing record rather than orphaning it.
    /// </summary>
    Task<AZOAResult<IActionGraphBundle>> GenerateAsync(Guid seriesId, Guid userId, CancellationToken ct = default,
        DeploymentGenerateRequest? request = null, Guid? actingTenantId = null);

    Task<AZOAResult<DeploymentReceipt>> DeployAsync(Guid seriesId, Guid userId, DeploymentStartRequest request,
        string requestKey, Guid? actingTenantId = null, CancellationToken ct = default);
}
