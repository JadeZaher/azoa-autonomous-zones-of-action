using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealSwapOperationStore
{
    /// <inheritdoc/>
    public async Task<SwapStageRecord?> RecordObservationAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, SwapStageObservation observation, CancellationToken ct = default)
    {
        var operation = await GetAsync(operationId, ct);
        var stage = await GetStageAsync(stageId, ct);
        if (operation is null || operation.CostAccountingVersion != 1 || stage is null || stage.OperationId != operationId || stage.IntentHash != operation.IntentHash ||
            (stage.AuthorizedAt is null && stage.ExternalExposedAt is null)) return null;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var plan = JsonSerializer.Deserialize<SwapStagePlan>(stage.PlanJson)!;
        // Operator settlements enter only through SettleOperatorReviewAsync, never through automatic observation.
        if (SwapOperatorSettlementContract.IsOperatorSettlement(observation) || !ValidJournalObservation(intent, stage, observation)) return null;
        var observationJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(observation));
        if (SwapObservationContract.IsTerminal(stage.State))
            return stage.ObservationJson == observationJson ? stage : null;
        if (operation.Version != expectedOperationVersion || operation.CurrentStageId != stageId ||
            operation.State is not ("Pending" or "NeedsOperator") || stage.Version != expectedStageVersion ||
            stage.State is not ("SubmissionAuthorized" or "ExternalExposed" or "NeedsOperator")) return null;
        var now = DateTimeOffset.UtcNow;
        var previousOperationState = operation.State;
        var previousStageState = stage.State;
        var nextOperationState = observation.Verdict == "NeedsOperator" ? "NeedsOperator" :
            SwapObservationContract.IsTerminal(observation.Verdict) ? "StageResolved" : "Pending";
        var nextStageState = observation.Verdict == "Pending" ?
            stage.AuthorizedAt is null ? "ExternalExposed" : "SubmissionAuthorized" : observation.Verdict;
        var operationMutation = SurrealWriter.UpdateOnly<SwapOperationRecord>(operationId)
            .Where(row => row.Version == expectedOperationVersion && row.State == previousOperationState && row.CurrentStageId == stageId)
            .Set(row => row.State, nextOperationState).Set(row => row.Version, expectedOperationVersion + 1)
            .Set(row => row.UpdatedAt, now).Set(row => row.NextAttemptAt, now.AddSeconds(15));
        if (SwapObservationContract.IsTerminal(observation.Verdict))
        {
            if (!UnsignedIntegerText.TryParse(operation.ConfirmedEffectNetworkFee, out var effectFee) ||
                !UnsignedIntegerText.TryParse(operation.ConfirmedCleanupNetworkFee, out var cleanupFee) ||
                !UnsignedIntegerText.TryParse(operation.ConfirmedRetainedRent, out var rent) ||
                !UnsignedIntegerText.TryParse(operation.UnresolvedRentReservation, out var unresolvedRent) ||
                !UnsignedIntegerText.TryParse(observation.NetworkFeePaid, out var stageFee)) return null;
            if (plan.Purpose == "Effect") effectFee += stageFee;
            else if (plan.Purpose == "Cleanup") cleanupFee += stageFee;
            else return null;
            var unresolvedCount = operation.UnresolvedRentStageCount;
            if (observation.LockedBalance is null)
            {
                if (!UnsignedIntegerText.TryParse(plan.ReservedLockedBalance, out var reservedRent)) return null;
                unresolvedRent += reservedRent;
                unresolvedCount = checked(unresolvedCount + 1);
            }
            else
            {
                if (!UnsignedIntegerText.TryParse(observation.LockedBalance, out var stageRent)) return null;
                rent += stageRent;
            }
            operationMutation = operationMutation
                .Set(row => row.ConfirmedEffectNetworkFee, effectFee.ToString(CultureInfo.InvariantCulture))
                .Set(row => row.ConfirmedCleanupNetworkFee, cleanupFee.ToString(CultureInfo.InvariantCulture))
                .Set(row => row.ConfirmedRetainedRent, rent.ToString(CultureInfo.InvariantCulture))
                .Set(row => row.UnresolvedRentReservation, unresolvedRent.ToString(CultureInfo.InvariantCulture))
                .Set(row => row.UnresolvedRentStageCount, unresolvedCount);
        }
        var stageMutation = SurrealWriter.UpdateOnly<SwapStageRecord>(stageId)
            .Where(row => row.Version == expectedStageVersion && row.State == previousStageState)
            .Set(row => row.State, nextStageState).Set(row => row.ObservationJson, observationJson)
            .Set(row => row.Version, expectedStageVersion + 1).Set(row => row.UpdatedAt, now).Build();
        return await CommitStageAsync([], operationMutation.Build(), stageMutation, ct);
    }

    private static bool ValidJournalObservation(SwapIntent intent, SwapStageRecord stage, SwapStageObservation observation)
    {
        if (CanonicalJson.HashCanonical(stage.PlanJson) != stage.PlanHash) return false;
        var plan = JsonSerializer.Deserialize<SwapStagePlan>(stage.PlanJson)!;
        if (SwapOperatorSettlementContract.IsOperatorSettlement(observation))
            return SwapObservationContract.IsTerminal(stage.State) && SwapOperatorSettlementContract.IsValid(intent, plan, observation);
        if (stage.PreparedJson is { } json)
        {
            var prepared = JsonSerializer.Deserialize<PreparedSwapStage>(json)!;
            return CanonicalJson.HashCanonical(json) == stage.PreparedHash && CanonicalJson.Hash(prepared.Plan) == stage.PlanHash &&
                SwapObservationContract.IsValid(intent, prepared, observation);
        }
        if (stage.ExternalExposedAt is null || stage.ExternalTransactionIdsJson is null ||
            observation.Verdict is not ("Pending" or "NeedsOperator" or "ExpiredAbsent")) return false;
        var ids = JsonSerializer.Deserialize<string[]>(stage.ExternalTransactionIdsJson)!;
        return SwapObservationContract.IsValid(intent, plan, ids, observation);
    }
}
