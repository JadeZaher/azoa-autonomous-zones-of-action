using System.Text.Json;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Dex;

namespace AZOA.WebAPI.Services.Dex;

/// <summary>Observes original authorized wire and advances bounded accounting history without new signatures or quotes.</summary>
public sealed class SwapRecoveryService(ISwapOperationStore operations, DexModuleResolver modules, SwapStageCoordinator stages)
{
    public async Task ReconcileAsync(string operationId, CancellationToken ct = default)
    {
        var operation = await operations.GetAsync(operationId, ct);
        if (operation is null) return;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var module = modules.Resolve(intent.Terms.Network);
        if (module is null) return;
        await stages.RecoverAuthorizedAsync(operationId, module, ct);
        await stages.FinalizeObservedAsync(operationId, module, ct);
        await stages.ReleaseRetainedResourcesAsync(operationId, module, ct);
        operation = await operations.GetAsync(operationId, ct);
        if (operation?.CurrentStageId is not { } currentId) return;
        var page = await operations.ReadHistoryPageAsync(operationId, operation.RecoveryHistoryCursor ?? currentId, ct);
        if (page.Count == 0) return;
        if (!await operations.SaveRecoveryCursorAsync(operationId, operation.Version, page[^1].PreviousStageId, ct)) return;
        var current = await operations.GetStageAsync(currentId, ct);
        var cleanup = current?.State == "Confirmed" && JsonSerializer.Deserialize<SwapStagePlan>(current.PlanJson)?.Purpose == "Cleanup";
        foreach (var original in page)
        {
            ct.ThrowIfCancellationRequested();
            if (original.State != "Confirmed" || (original.AuthorizedAt is null && original.ExternalExposedAt is null) || original.ObservationJson is null) continue;
            var id = SurrealRecordGuid.BareId(original.Id);
            var observation = JsonSerializer.Deserialize<SwapStageObservation>(original.ObservationJson)!;
            if (observation.LockedBalance is null && original.ResolvedLockedBalance is null)
                await stages.ResolveAccountingAsync(operationId, id, module, ct);
            if (cleanup && id != currentId && original.ReleasedLockedBalance is null &&
                JsonSerializer.Deserialize<SwapStagePlan>(original.PlanJson)?.Purpose == "Effect")
            {
                var latest = await operations.GetStageAsync(id, ct);
                if (UnsignedIntegerText.TryParse(latest?.ResolvedLockedBalance ?? observation.LockedBalance, out var actual) && actual > 0)
                    await stages.ResolveRentReleaseAsync(operationId, id, currentId, module, ct);
            }
        }
        if (!cleanup) return;
        operation = await operations.GetAsync(operationId, ct);
        current = await operations.GetStageAsync(currentId, ct);
        if (operation is not null && current is not null)
            await operations.CompleteCleanupAsync(operationId, operation.Version, currentId, current.Version, ct);
    }
}
