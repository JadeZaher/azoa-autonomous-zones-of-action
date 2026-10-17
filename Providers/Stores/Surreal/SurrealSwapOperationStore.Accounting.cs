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
    public async Task<SwapStageRecord?> ResolveRentAccountingAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, string actualLockedBalance, string evidenceJson, CancellationToken ct = default)
    {
        var operation = await GetAsync(operationId, ct);
        var stage = await GetStageAsync(stageId, ct);
        if (operation is null || operation.CostAccountingVersion != 1 || stage is null || stage.OperationId != operationId ||
            stage.IntentHash != operation.IntentHash || stage.State != "Confirmed" || stage.PreparedJson is null ||
            stage.ObservationJson is null || !SwapObservationContract.HasEvidence(evidenceJson) ||
            !UnsignedIntegerText.TryParse(actualLockedBalance, out var actual)) return null;
        using var evidence = JsonDocument.Parse(evidenceJson);
        var canonicalEvidence = CanonicalJson.Canonicalize(evidence.RootElement);
        if (stage.ResolvedLockedBalance is not null)
            return stage.ResolvedLockedBalance == actualLockedBalance && stage.RentAccountingEvidenceJson == canonicalEvidence ? stage : null;
        var observation = JsonSerializer.Deserialize<SwapStageObservation>(stage.ObservationJson)!;
        var prepared = JsonSerializer.Deserialize<PreparedSwapStage>(stage.PreparedJson)!;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        if (operation.Version != expectedOperationVersion || stage.Version != expectedStageVersion ||
            observation.LockedBalance is not null || !SwapObservationContract.IsValid(intent, prepared, observation) ||
            !UnsignedIntegerText.TryParse(prepared.Plan.ReservedLockedBalance, out var reservation) || actual > reservation ||
            !UnsignedIntegerText.TryParse(operation.UnresolvedRentReservation, out var outstanding) || outstanding < reservation ||
            !UnsignedIntegerText.TryParse(operation.ConfirmedRetainedRent, out var known) || operation.UnresolvedRentStageCount <= 0) return null;
        var now = DateTimeOffset.UtcNow;
        var mutation = SurrealWriter.UpdateOnly<SwapOperationRecord>(operationId)
            .Where(row => row.Version == expectedOperationVersion && row.CostAccountingVersion == 1)
            .Set(row => row.UnresolvedRentReservation, (outstanding - reservation).ToString(CultureInfo.InvariantCulture))
            .Set(row => row.ConfirmedRetainedRent, (known + actual).ToString(CultureInfo.InvariantCulture))
            .Set(row => row.UnresolvedRentStageCount, operation.UnresolvedRentStageCount - 1)
            .Set(row => row.Version, expectedOperationVersion + 1).Set(row => row.UpdatedAt, now).Build();
        var stageMutation = SurrealWriter.UpdateOnly<SwapStageRecord>(stageId)
            .Where(row => row.Version == expectedStageVersion && row.State == "Confirmed" && row.ResolvedLockedBalance == null)
            .Set(row => row.ResolvedLockedBalance, actualLockedBalance).Set(row => row.RentAccountingEvidenceJson, canonicalEvidence)
            .Set(row => row.Version, expectedStageVersion + 1).Set(row => row.UpdatedAt, now).Build();
        return await CommitStageAsync([], mutation, stageMutation, ct);
    }
}
