using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Dex;

public sealed partial class SwapStageCoordinator
{
    public async Task<AZOAResult<SwapStageRecord>> ExposeAsync(string operationId, long operationVersion,
        string stageId, long stageVersion, bool acceptPotentialExternalSubmission, SigningAdmissionAuthority authority,
        IDexTransactionModule module, CancellationToken ct = default)
    {
        if (!acceptPotentialExternalSubmission) return Refused();
        var operation = await store.GetAsync(operationId, ct);
        var stage = await store.GetStageAsync(stageId, ct);
        if (!TryRead(operation, stage, module, out var intent, out var plan)) return Refused();
        var identities = module.GetExposedSwapTransactionIds(intent, plan);
        if (identities.IsError || identities.Result is null)
            return AZOAResult<SwapStageRecord>.Failure(identities.Message);
        var hash = CanonicalJson.Hash(new { domain = "swap-external-exposure/1", operationId, plan });
        var saved = await store.ExposeStageAsync(operationId, operationVersion, stageId, stageVersion,
            hash, identities.Result, authority, ct);
        return saved is null ? Refused() : AZOAResult<SwapStageRecord>.Success(saved);
    }

    private async Task<SwapStageRecord?> RecoverExposedAsync(string operationId, IDexTransactionModule module,
        CancellationToken ct)
    {
        var operation = await store.GetAsync(operationId, ct);
        if (operation?.CurrentStageId is not { } stageId) return null;
        var stage = await store.GetStageAsync(stageId, ct);
        if (!TryRead(operation, stage, module, out var intent, out var plan) ||
            operation.State is not ("Pending" or "NeedsOperator") || stage!.ExternalExposedAt is null ||
            stage.AuthorizedAt is not null || stage.State is not ("ExternalExposed" or "NeedsOperator")) return stage;
        if (stage.PreparedJson is not null)
        {
            if (!TryPrepared(stage, plan, module, intent, out var candidate)) return stage;
            var observation = await module.ObserveSwapStageAsync(intent, candidate, ct, ReadSavedObservation(stage));
            return observation.IsError || observation.Result is null || !ValidFinalizedCandidate(module, intent, candidate, observation.Result) ? stage :
                await store.RecordObservationAsync(operationId, operation.Version, stageId, stage.Version, observation.Result, ct);
        }
        var external = await module.ObserveExposedSwapStageAsync(intent, plan, ct, ReadSavedObservation(stage));
        if (external.IsError || external.Result is null) return stage;
        var result = external.Result;
        if (result.RecoveredCandidate is { } recovered)
        {
            var validated = module.ValidateSignedSwapStage(intent, plan, recovered.SignedPayloadJson);
            if (validated.IsError || validated.Result is not { } actual ||
                actual.SignedPayloadJson != recovered.SignedPayloadJson ||
                !actual.TransactionIds.SequenceEqual(recovered.TransactionIds)) return stage;
            var saved = await store.SaveExposedCandidateAsync(operationId, operation.Version, stageId, stage.Version, actual, ct);
            if (saved is null) return stage;
            stage = saved;
            operation = await store.GetAsync(operationId, ct);
            if (operation?.CurrentStageId != stageId) return stage;
        }
        else if (result.Observation.Verdict is not ("Pending" or "NeedsOperator") &&
            !module.ValidateExposedSwapNoEffect(intent, plan, result.Observation)) return stage;
        if (result.Observation.FinalizedCandidate is not null &&
            (!TryPrepared(stage, plan, module, intent, out var original) || !ValidFinalizedCandidate(module, intent, original, result.Observation))) return stage;
        return await store.RecordObservationAsync(operationId, operation.Version, stageId, stage.Version, result.Observation, ct);
    }

    private static SwapStageObservation? ReadSavedObservation(SwapStageRecord stage)
        => stage.ObservationJson is null ? null : JsonSerializer.Deserialize<SwapStageObservation>(stage.ObservationJson);
}
