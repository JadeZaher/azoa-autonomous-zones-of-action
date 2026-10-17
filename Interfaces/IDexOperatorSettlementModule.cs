using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

/// <summary>Optional operator-only settlement of a stage whose finalized outcome automatic observation could not classify.</summary>
public interface IDexOperatorSettlementModule
{
    /// <summary>Re-proves the saved operator-review evidence from finalized chain state and returns the exact terminal outcome with the audit.</summary>
    Task<AZOAResult<SwapStageObservation>> ProveOperatorSettlementAsync(SwapIntent intent, SwapStagePlan plan,
        SwapStageObservation review, SwapOperatorSettlement audit, CancellationToken ct = default);

    /// <summary>Validates a recorded operator settlement against its plan without network access, for history replay.</summary>
    bool ValidateOperatorSettlement(SwapIntent intent, SwapStagePlan plan, SwapStageObservation settled);

    /// <summary>Idempotently releases provider resources the recorded confirmed settlement's own evidence proves free.</summary>
    Task<bool> ReleaseSettledResourcesAsync(SwapIntent intent, string stageId, SwapStagePlan plan,
        SwapStageObservation settled, CancellationToken ct = default);
}
