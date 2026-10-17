using AZOA.WebAPI.Models.Deployments;

namespace AZOA.WebAPI.Helpers.Deployments;

/// <summary>Projects confirmed economic effects independently of workflow completion.</summary>
public static class NativeResourceEffects
{
    public static NativeResourceEffect? FromHistory(IReadOnlyList<NativeStageHistory> history)
        => history.LastOrDefault(item => item.Observation.Verdict == "Confirmed" && item.Observation.ResourceEffect is not null)?.Observation.ResourceEffect;

    public static NativeResourceEffect? CleanupFromHistory(IReadOnlyList<NativeStageHistory> history)
        => history.LastOrDefault(item => item.Observation.Verdict == "Confirmed" && item.Observation.CleanupEffect is not null)?.Observation.CleanupEffect;
}
