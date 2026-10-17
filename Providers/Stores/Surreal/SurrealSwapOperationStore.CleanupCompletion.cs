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
    public async Task<SwapOperationRecord?> CompleteCleanupAsync(string operationId, long expectedOperationVersion,
        string cleanupStageId, long expectedStageVersion, CancellationToken ct = default)
    {
        var operation = await GetAsync(operationId, ct);
        var stage = await GetStageAsync(cleanupStageId, ct);
        if (operation is null || stage is null || operation.CurrentStageId != cleanupStageId || !operation.CleanupStarted ||
            operation.CleanupConsentJson is null || stage.OperationId != operationId || stage.IntentHash != operation.IntentHash ||
            stage.State != "Confirmed" || (stage.AuthorizedAt is null && stage.ExternalExposedAt is null) || stage.PreparedJson is null || stage.ObservationJson is null ||
            operation.CostAccountingVersion != 1 || operation.UnresolvedRentStageCount != 0 || operation.UnresolvedRentReservation != "0" ||
            !UnsignedIntegerText.TryParse(operation.ConfirmedRetainedRent, out var allocated) ||
            !UnsignedIntegerText.TryParse(operation.ConfirmedReleasedRent, out var released) || released != allocated) return null;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var prepared = JsonSerializer.Deserialize<PreparedSwapStage>(stage.PreparedJson)!;
        var observation = JsonSerializer.Deserialize<SwapStageObservation>(stage.ObservationJson)!;
        if (prepared.Plan.Purpose != "Cleanup" || prepared.Plan.CleanupConsent is not { } consent || !SwapCleanupContract.IsValid(intent, consent) ||
            operation.CleanupConsentJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(consent)) ||
            !SwapObservationContract.IsValid(intent, prepared, observation) || observation.ActualInput != "0" ||
            observation.ActualOutput != "0" || observation.PlatformFeePaid != "0" || observation.LockedBalance != "0") return null;
        var receipt = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new SwapCleanupReceipt(operationId, operation.QuoteHash,
            cleanupStageId, CanonicalJson.Hash(consent), observation.TransactionIds, operation.ConfirmedReleasedRent,
            operation.SettlementReceiptJson is not null)));
        if (operation.CleanupReceiptJson is not null)
            return operation.State == "CleanupCompleted" && operation.CleanupReceiptJson == receipt ? operation : null;
        if (operation.Version != expectedOperationVersion || operation.State != "StageResolved" || stage.Version != expectedStageVersion) return null;
        var now = DateTimeOffset.UtcNow;
        var opMutation = SurrealWriter.UpdateOnly<SwapOperationRecord>(operationId)
            .Where(row => row.Version == expectedOperationVersion && row.State == "StageResolved" && row.CurrentStageId == cleanupStageId && row.CleanupReceiptJson == null)
            .Set(row => row.CleanupReceiptJson, receipt).Set(row => row.State, "CleanupCompleted")
            .Set(row => row.Version, expectedOperationVersion + 1).Set(row => row.UpdatedAt, now).Build();
        var stageMutation = SurrealWriter.UpdateOnly<SwapStageRecord>(cleanupStageId)
            .Where(row => row.Version == expectedStageVersion && row.State == "Confirmed")
            .Set(row => row.Version, expectedStageVersion + 1).Set(row => row.UpdatedAt, now).Build();
        return await CommitStageAsync([], opMutation, stageMutation, ct) is null ? null : await GetAsync(operationId, ct);
    }
}
