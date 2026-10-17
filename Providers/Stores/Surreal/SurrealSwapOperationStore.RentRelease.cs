using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealSwapOperationStore
{
    /// <inheritdoc/>
    public async Task<SwapStageRecord?> RecordRentReleaseAsync(string operationId, long expectedOperationVersion,
        string allocationStageId, long expectedAllocationVersion, string cleanupStageId, long expectedCleanupVersion,
        SwapRentReleaseProgress progress, CancellationToken ct = default)
    {
        if (allocationStageId == cleanupStageId || !SwapObservationContract.HasEvidence(progress.CheckpointJson) ||
            (progress.Complete ? !SwapObservationContract.HasEvidence(progress.EvidenceJson) ||
                !UnsignedIntegerText.TryParse(progress.ReleasedLockedBalance, out _) :
                progress.ReleasedLockedBalance is not null || progress.EvidenceJson is not null)) return null;
        var operation = await GetAsync(operationId, ct);
        var allocation = await GetStageAsync(allocationStageId, ct);
        var cleanup = await GetStageAsync(cleanupStageId, ct);
        if (operation is null || operation.CostAccountingVersion != 1 || allocation is null || cleanup is null ||
            allocation.OperationId != operationId || cleanup.OperationId != operationId ||
            allocation.IntentHash != operation.IntentHash || cleanup.IntentHash != operation.IntentHash ||
            allocation.State != "Confirmed" || cleanup.State != "Confirmed" || (allocation.AuthorizedAt is null && allocation.ExternalExposedAt is null) || (cleanup.AuthorizedAt is null && cleanup.ExternalExposedAt is null) ||
            allocation.PreparedJson is null || cleanup.PreparedJson is null || allocation.ObservationJson is null || cleanup.ObservationJson is null ||
            (allocation.RentReleaseCleanupStageId is not null && allocation.RentReleaseCleanupStageId != cleanupStageId)) return null;
        using var checkpointDocument = JsonDocument.Parse(progress.CheckpointJson);
        var checkpoint = CanonicalJson.Canonicalize(checkpointDocument.RootElement);
        var evidence = progress.EvidenceJson is { } raw ? CanonicalJson.Canonicalize(JsonSerializer.Deserialize<JsonElement>(raw)) : null;
        if (allocation.ReleasedLockedBalance is not null)
            return progress.Complete && allocation.ReleasedLockedBalance == progress.ReleasedLockedBalance &&
                allocation.RentReleaseEvidenceJson == evidence && allocation.RentReleaseScanJson == checkpoint ? allocation : null;
        if (operation.Version != expectedOperationVersion || allocation.Version != expectedAllocationVersion || cleanup.Version != expectedCleanupVersion)
            return null;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var allocated = JsonSerializer.Deserialize<PreparedSwapStage>(allocation.PreparedJson)!;
        var closed = JsonSerializer.Deserialize<PreparedSwapStage>(cleanup.PreparedJson)!;
        var allocationObservation = JsonSerializer.Deserialize<SwapStageObservation>(allocation.ObservationJson)!;
        var cleanupObservation = JsonSerializer.Deserialize<SwapStageObservation>(cleanup.ObservationJson)!;
        if (allocated.Plan.Purpose != "Effect" || closed.Plan.Purpose != "Cleanup" || closed.Plan.CleanupConsent is not { } consent ||
            operation.CleanupConsentJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(consent)) ||
            !SwapCleanupContract.IsValid(intent, consent) || !SwapObservationContract.IsValid(intent, allocated, allocationObservation) ||
            !SwapObservationContract.IsValid(intent, closed, cleanupObservation) ||
            !UnsignedIntegerText.TryParse(allocation.ResolvedLockedBalance ?? allocationObservation.LockedBalance, out var actual) || actual <= 0 ||
            !UnsignedIntegerText.TryParse(operation.ConfirmedRetainedRent, out var total) ||
            !UnsignedIntegerText.TryParse(operation.ConfirmedReleasedRent, out var released) || released > total) return null;
        if (progress.Complete && (!UnsignedIntegerText.TryParse(progress.ReleasedLockedBalance, out var release) ||
            release != actual || released + release > total)) return null;
        var now = DateTimeOffset.UtcNow;
        var opMutation = SurrealWriter.UpdateOnly<SwapOperationRecord>(operationId)
            .Where(row => row.Version == expectedOperationVersion && row.CostAccountingVersion == 1)
            .Set(row => row.Version, expectedOperationVersion + 1).Set(row => row.UpdatedAt, now);
        var allocationMutation = SurrealWriter.UpdateOnly<SwapStageRecord>(allocationStageId)
            .Where(row => row.Version == expectedAllocationVersion && row.State == "Confirmed" && row.ReleasedLockedBalance == null)
            .Set(row => row.RentReleaseCleanupStageId, cleanupStageId).Set(row => row.RentReleaseScanJson, checkpoint)
            .Set(row => row.Version, expectedAllocationVersion + 1).Set(row => row.UpdatedAt, now);
        if (progress.Complete)
        {
            opMutation = opMutation.Set(row => row.ConfirmedReleasedRent, (released + actual).ToString(CultureInfo.InvariantCulture));
            allocationMutation = allocationMutation.Set(row => row.ReleasedLockedBalance, progress.ReleasedLockedBalance!)
                .Set(row => row.RentReleaseEvidenceJson, evidence!);
        }
        var cleanupMutation = SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<SwapStageRecord>(cleanupStageId)
            .Where(row => row.Version == expectedCleanupVersion && row.State == "Confirmed")
            .Set(row => row.Version, expectedCleanupVersion + 1).Set(row => row.UpdatedAt, now).Build(), "swap_release_cleanup", "_swap_release_cleanup");
        // raw: original allocation, finalized cleanup witness and release aggregate share one atomic epoch transition.
        SurrealQuery[] guards = [
            cleanupMutation,
            SurrealQuery.Of("IF $_swap_release_cleanup = NONE OR $_swap_release_cleanup = [] { THROW 'SWAP_ADMISSION_CONFLICT' }")];
        return await CommitStageAsync(guards, opMutation.Build(), allocationMutation.Build(), ct);
    }
}
