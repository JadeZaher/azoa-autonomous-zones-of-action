using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

/// <summary>Plans explicitly authorized recovery without changing the original intent or quoted limits.</summary>
public interface INativeRecoveryModule
{
    Task<AZOAResult<NativeNextStage>> GetRecoveryNextStageAsync(NativeOperationIntent intent,
        IReadOnlyList<NativeStageHistory> history, string mode, CancellationToken ct = default);
}
