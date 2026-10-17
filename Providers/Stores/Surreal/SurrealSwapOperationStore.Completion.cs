using System.Text.Json;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealSwapOperationStore
{
    /// <inheritdoc/>
    public async Task<SwapOperationRecord?> CompleteAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, IReadOnlyList<DexEffectDisclosure> retainedEffects, CancellationToken ct = default)
    {
        var operation = await GetAsync(operationId, ct);
        var stage = await GetStageAsync(stageId, ct);
        if (operation is null || stage is null || operation.CostAccountingVersion != 1 || stage.OperationId != operationId ||
            operation.CurrentStageId != stageId || stage.IntentHash != operation.IntentHash || stage.State != "Confirmed" ||
            (stage.AuthorizedAt is null && stage.ExternalExposedAt is null) || stage.ObservationJson is null ||
            retainedEffects is null || retainedEffects.Any(effect => effect is null)) return null;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var observation = JsonSerializer.Deserialize<SwapStageObservation>(stage.ObservationJson)!;
        if (!ProvenConfirmedStage(intent, stage, observation, out var plan, out var prepared) || plan.Purpose != "Effect" ||
            !UnsignedIntegerText.TryParse(intent.Terms.GrossInput, out var gross) || gross <= 0 ||
            observation.ActualInput != intent.Terms.GrossInput || observation.PlatformFeePaid != intent.Terms.PlatformFee.Amount ||
            !UnsignedIntegerText.TryParse(observation.ActualOutput, out var output) ||
            !UnsignedIntegerText.TryParse(intent.Terms.MinimumOutput, out var minimum) || output < minimum) return null;
        var receipt = new SwapSettlementReceipt(operationId, operation.QuoteHash, stageId, intent.Terms.Network,
            observation.TransactionIds, observation.ActualInput!, observation.ActualOutput!, observation.PlatformFeePaid!, retainedEffects,
            stage.PreparedHash, stage.PlanHash, prepared?.TransactionIds);
        var receiptJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(receipt));
        if (operation.State == "Completed") return operation.SettlementReceiptJson == receiptJson ? operation : null;
        if (operation.State != "StageResolved" || operation.Version != expectedOperationVersion || stage.Version != expectedStageVersion ||
            operation.SettlementReceiptJson is not null) return null;
        var now = DateTimeOffset.UtcNow;
        var operationMutation = SurrealWriter.UpdateOnly<SwapOperationRecord>(operationId)
            .Where(row => row.Version == expectedOperationVersion && row.State == "StageResolved" &&
                row.CurrentStageId == stageId && row.SettlementReceiptJson == null)
            .Set(row => row.State, "Completed").Set(row => row.SettlementReceiptJson, receiptJson)
            .Set(row => row.Version, expectedOperationVersion + 1).Set(row => row.UpdatedAt, now).Build();
        var stageMutation = SurrealWriter.UpdateOnly<SwapStageRecord>(stageId)
            .Where(row => row.Version == expectedStageVersion && row.State == "Confirmed")
            .Set(row => row.Version, expectedStageVersion + 1).Set(row => row.UpdatedAt, now).Build();
        return await CommitStageAsync([], operationMutation, stageMutation, ct) is null ? null : await GetAsync(operationId, ct);
    }

    /// <summary>A confirmed stage proves its outcome through its saved candidate or through a recorded operator settlement.</summary>
    private static bool ProvenConfirmedStage(SwapIntent intent, SwapStageRecord stage, SwapStageObservation observation,
        out SwapStagePlan plan, out PreparedSwapStage? prepared)
    {
        plan = JsonSerializer.Deserialize<SwapStagePlan>(stage.PlanJson)!;
        prepared = stage.PreparedJson is { } json ? JsonSerializer.Deserialize<PreparedSwapStage>(json) : null;
        if (observation.Verdict != "Confirmed" || CanonicalJson.HashCanonical(stage.PlanJson) != stage.PlanHash) return false;
        return SwapOperatorSettlementContract.IsOperatorSettlement(observation)
            ? SwapOperatorSettlementContract.IsValid(intent, plan, observation)
            : prepared is not null && CanonicalJson.Hash(prepared.Plan) == stage.PlanHash && SwapObservationContract.IsValid(intent, prepared, observation);
    }
}
