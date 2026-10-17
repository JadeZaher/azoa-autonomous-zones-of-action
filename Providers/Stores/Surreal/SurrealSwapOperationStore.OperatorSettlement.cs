using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealSwapOperationStore
{
    /// <inheritdoc/>
    public async Task<SwapStageRecord?> SettleOperatorReviewAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, SwapStageObservation settled, CancellationToken ct = default)
    {
        var operation = await GetAsync(operationId, ct);
        var stage = await GetStageAsync(stageId, ct);
        if (operation is null || stage is null || operation.CostAccountingVersion != 1 || stage.OperationId != operationId ||
            stage.IntentHash != operation.IntentHash || operation.CurrentStageId != stageId || stage.ObservationJson is null ||
            (stage.AuthorizedAt is null && stage.ExternalExposedAt is null) || CanonicalJson.HashCanonical(stage.PlanJson) != stage.PlanHash) return null;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var plan = JsonSerializer.Deserialize<SwapStagePlan>(stage.PlanJson)!;
        var settledJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(settled));
        if (SwapObservationContract.IsTerminal(stage.State)) return stage.ObservationJson == settledJson ? stage : null;
        // The audit binds the exact operator-review observation it replaces; a re-observed or different review is refused.
        if (SwapOperatorSettlementContract.ReadAudit(settled) is not { } audit ||
            audit.ReplacedObservationHash != CanonicalJson.HashCanonical(stage.ObservationJson) ||
            !SwapOperatorSettlementContract.IsValid(intent, plan, settled) || operation.Version != expectedOperationVersion ||
            operation.State != "NeedsOperator" || stage.Version != expectedStageVersion || stage.State != "NeedsOperator" ||
            !UnsignedIntegerText.TryParse(operation.ConfirmedEffectNetworkFee, out var effectFee) ||
            !UnsignedIntegerText.TryParse(operation.ConfirmedCleanupNetworkFee, out var cleanupFee) ||
            !UnsignedIntegerText.TryParse(operation.ConfirmedRetainedRent, out var rent) ||
            !UnsignedIntegerText.TryParse(settled.NetworkFeePaid, out var stageFee) ||
            !UnsignedIntegerText.TryParse(settled.LockedBalance, out var stageRent)) return null;
        if (plan.Purpose == "Effect") effectFee += stageFee;
        else if (plan.Purpose == "Cleanup") cleanupFee += stageFee;
        else return null;
        rent += stageRent;
        var now = DateTimeOffset.UtcNow;
        var operationMutation = SurrealWriter.UpdateOnly<SwapOperationRecord>(operationId)
            .Where(row => row.Version == expectedOperationVersion && row.State == "NeedsOperator" && row.CurrentStageId == stageId)
            .Set(row => row.State, "StageResolved").Set(row => row.Version, expectedOperationVersion + 1)
            .Set(row => row.ConfirmedEffectNetworkFee, effectFee.ToString(CultureInfo.InvariantCulture))
            .Set(row => row.ConfirmedCleanupNetworkFee, cleanupFee.ToString(CultureInfo.InvariantCulture))
            .Set(row => row.ConfirmedRetainedRent, rent.ToString(CultureInfo.InvariantCulture))
            .Set(row => row.UpdatedAt, now).Set(row => row.NextAttemptAt, now).Build();
        var stageMutation = SurrealWriter.UpdateOnly<SwapStageRecord>(stageId)
            .Where(row => row.Version == expectedStageVersion && row.State == "NeedsOperator")
            .Set(row => row.State, settled.Verdict).Set(row => row.ObservationJson, settledJson)
            .Set(row => row.Version, expectedStageVersion + 1).Set(row => row.UpdatedAt, now).Build();
        return await CommitStageAsync([], operationMutation, stageMutation, ct);
    }
}
