using AZOA.WebAPI.Models.Dex;

namespace AZOA.WebAPI.Interfaces;

/// <summary>Optional DEX capability for provider-owned durable resources that a persisted confirmed stage can prove free.</summary>
public interface IDexRetainedResourceModule
{
    /// <summary>Idempotently releases resources only against the saved confirmed stage's own evidence; false keeps them held.</summary>
    Task<bool> ReleaseRetainedResourcesAsync(SwapIntent intent, string stageId, PreparedSwapStage prepared,
        SwapStageObservation observation, CancellationToken ct = default);

    /// <summary>Releases reservations of a cancelled stage that was never signed, exposed or authorized; signed ones stay held.</summary>
    Task<bool> ReleaseUnsentStageAsync(SwapIntent intent, SwapStagePlan plan, CancellationToken ct = default);
}
