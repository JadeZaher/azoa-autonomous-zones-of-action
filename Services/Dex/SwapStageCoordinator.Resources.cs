using System.Text.Json;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;

namespace AZOA.WebAPI.Services.Dex;

public sealed partial class SwapStageCoordinator
{
    /// <summary>Lets the original provider release durable resources proven free by the current saved confirmed stage.</summary>
    public async Task<bool> ReleaseRetainedResourcesAsync(string operationId, IDexTransactionModule module, CancellationToken ct = default)
    {
        var operation = await store.GetAsync(operationId, ct);
        if (operation?.CurrentStageId is not { } stageId) return false;
        var stage = await store.GetStageAsync(stageId, ct);
        if (!TryRead(operation, stage, module, out var intent, out var plan) || stage!.State != "Confirmed" || stage.ObservationJson is null ||
            (stage.AuthorizedAt is null && stage.ExternalExposedAt is null)) return false;
        var observation = JsonSerializer.Deserialize<SwapStageObservation>(stage.ObservationJson)!;
        if (SwapOperatorSettlementContract.IsOperatorSettlement(observation))
            return module is IDexOperatorSettlementModule settlement &&
                await settlement.ReleaseSettledResourcesAsync(intent, stageId, plan, observation, ct);
        if (module is not IDexRetainedResourceModule resources || !TryPrepared(stage, plan, module, intent, out var prepared)) return false;
        return SwapObservationContract.IsValid(intent, prepared, observation) && ValidFinalizedCandidate(module, intent, prepared, observation) &&
            await resources.ReleaseRetainedResourcesAsync(intent, stageId, prepared, observation, ct);
    }

    /// <summary>Returns provider reservations of a cancelled stage that never held a signature, exposure or send authority.</summary>
    public async Task<bool> ReleaseUnsentStageAsync(string operationId, string stageId, IDexTransactionModule module, CancellationToken ct = default)
    {
        if (module is not IDexRetainedResourceModule resources) return false;
        var operation = await store.GetAsync(operationId, ct);
        var stage = await store.GetStageAsync(stageId, ct);
        if (!TryRead(operation, stage, module, out var intent, out var plan) || stage!.State != "Cancelled" || stage.PreparedJson is not null ||
            stage.AuthorizedAt is not null || stage.ExternalExposedAt is not null) return false;
        return await resources.ReleaseUnsentStageAsync(intent, plan, ct);
    }
}
