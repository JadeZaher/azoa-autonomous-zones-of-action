using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealSwapOperationStore
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<SwapStageRecord>> ReadHistoryPageAsync(string operationId, string? cursor = null,
        CancellationToken ct = default)
    {
        var operation = await GetAsync(operationId, ct);
        if (operation is null) return [];
        var next = cursor ?? operation.CurrentStageId;
        var result = new List<SwapStageRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (next is not null && result.Count < 25)
        {
            if (!seen.Add(next)) throw new InvalidOperationException("Swap stage ancestry contains a cycle.");
            var stage = await GetStageAsync(next, ct);
            if (stage is null || stage.OperationId != operationId || stage.IntentHash != operation.IntentHash) return [];
            result.Add(stage);
            next = stage.PreviousStageId;
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<SwapStageRecord?> ClaimNextStageAsync(string operationId, long expectedVersion,
        SwapStagePlan plan, SigningAdmissionAuthority authority, CancellationToken ct = default)
    {
        var operation = await GetAsync(operationId, ct);
        if (operation is null) return null;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        if (!StagePrincipal(intent, plan, authority) || plan.Purpose is not ("Effect" or "Cleanup") || plan.Generation < 0 ||
            string.IsNullOrWhiteSpace(plan.Stage) || plan.IntentHash != operation.IntentHash ||
            plan.PreparationKey != (plan.Purpose == "Cleanup" && plan.CleanupConsent is { } cleanup
                ? SwapOperationIdentity.CleanupPreparation(operationId, plan.Stage, plan.Generation, operation.IntentHash, cleanup)
                : SwapOperationIdentity.Preparation(operationId, plan.Stage, plan.Generation, operation.IntentHash)) ||
            string.IsNullOrEmpty(plan.UnsignedPayloadJson) || string.IsNullOrEmpty(plan.RecoveryMetadata) ||
            plan.RequiredSignerAddresses is not { Count: > 0 } || plan.RequiredSignerAddresses.Any(string.IsNullOrWhiteSpace)) return null;
        var stageId = SwapOperationIdentity.Stage(plan.PreparationKey);
        var planJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan));
        var existing = await GetStageAsync(stageId, ct);
        if (existing is not null) return existing.OperationId == operationId && existing.PlanJson == planJson ? existing : null;
        if (operation.CostAccountingVersion != 1 || operation.Version != expectedVersion ||
            operation.State is not ("StageResolved" or "CleanupReady") || operation.CurrentStageId is null ||
            (operation.State == "CleanupReady" && plan.Purpose != "Cleanup")) return null;
        var previous = await GetStageAsync(operation.CurrentStageId, ct);
        if (previous is null || previous.OperationId != operationId || previous.IntentHash != operation.IntentHash ||
            !WithinRemainingBudget(intent, operation, plan)) return null;
        var unsentCleanup = operation.State == "CleanupReady" && plan.Purpose == "Cleanup" &&
            previous.State == "Cancelled" && previous.AuthorizedAt is null && previous.ExternalExposedAt is null;
        if (!unsentCleanup)
        {
            if (!SwapObservationContract.IsTerminal(previous.State) || previous.ObservationJson is null ||
                (previous.AuthorizedAt is null && previous.ExternalExposedAt is null)) return null;
            var previousObservation = JsonSerializer.Deserialize<SwapStageObservation>(previous.ObservationJson)!;
            if (previousObservation.Verdict != previous.State || !ValidJournalObservation(intent, previous, previousObservation)) return null;
        }
        var previousPlan = JsonSerializer.Deserialize<SwapStagePlan>(previous.PlanJson)!;
        if (plan.Stage == previous.Stage)
        {
            if (previous.State is not ("FailedOnChain" or "ExpiredAbsent") || previous.Generation == int.MaxValue ||
                plan.Generation != previous.Generation + 1 || plan.Purpose != previousPlan.Purpose) return null;
        }
        else
        {
            if (plan.Generation != 0) return null;
            var name = plan.Stage;
            var priorUse = await executor.QueryAsync<SwapStageRecord>(SurrealQuery<SwapStageRecord>.From()
                .Where(row => row.OperationId == operationId && row.Stage == name).Limit(1), ct);
            if (priorUse.Count != 0) return null;
        }
        if (plan.Purpose == "Effect" && operation.CleanupStarted) return null;
        var guards = StageAuthority(operation, intent, plan, authority);
        if (guards is null) return null;
        var now = DateTimeOffset.UtcNow;
        var stage = new SwapStageRecord
        {
            Id = stageId, OperationId = operationId, IntentHash = operation.IntentHash, Stage = plan.Stage,
            Generation = plan.Generation, PreparationKey = plan.PreparationKey, PlanJson = planJson,
            PlanHash = CanonicalJson.HashCanonical(planJson), PreviousStageId = operation.CurrentStageId,
            CreatedAt = now, UpdatedAt = now,
        };
        var previousId = operation.CurrentStageId;
        var previousState = operation.State;
        var mutation = SurrealWriter.UpdateOnly<SwapOperationRecord>(operationId)
            .Where(row => row.Version == expectedVersion && row.State == previousState && row.CurrentStageId == previousId)
            .Set(row => row.CurrentStageId, stageId).Set(row => row.State, "Preparing")
            .Set(row => row.CleanupStarted, operation.CleanupStarted || plan.Purpose == "Cleanup")
            .Set(row => row.Version, expectedVersion + 1).Set(row => row.UpdatedAt, now).Build();
        return await CommitStageAsync(guards, mutation, SurrealWriter.Create(stage), ct);
    }

    private static bool WithinRemainingBudget(SwapIntent intent, SwapOperationRecord operation, SwapStagePlan next)
    {
        if (!UnsignedIntegerText.TryParse(next.ReservedNetworkFee, out var nextFee) ||
            !UnsignedIntegerText.TryParse(next.ReservedLockedBalance, out var nextRent) ||
            !UnsignedIntegerText.TryParse(intent.Terms.Budget.MaximumNetworkFee, out var effectCap) ||
            !UnsignedIntegerText.TryParse(intent.Terms.Budget.MaximumCleanupFee, out var cleanupCap) ||
            !UnsignedIntegerText.TryParse(intent.Terms.Budget.MaximumLockedBalance, out var rentCap) ||
            !UnsignedIntegerText.TryParse(operation.ConfirmedEffectNetworkFee, out var effectFee) ||
            !UnsignedIntegerText.TryParse(operation.ConfirmedCleanupNetworkFee, out var cleanupFee) ||
            !UnsignedIntegerText.TryParse(operation.ConfirmedRetainedRent, out var rent) ||
            !UnsignedIntegerText.TryParse(operation.ConfirmedReleasedRent, out var released) || released > rent ||
            !UnsignedIntegerText.TryParse(operation.UnresolvedRentReservation, out var unresolvedRent)) return false;
        if (next.Purpose == "Effect") effectFee += nextFee;
        else
        {
            if (next.CleanupConsent is not { } consent || !UnsignedIntegerText.TryParse(consent.MaximumNetworkFee, out var acceptedCleanupCap)) return false;
            cleanupCap = System.Numerics.BigInteger.Min(cleanupCap, acceptedCleanupCap);
            cleanupFee += nextFee;
        }
        return effectFee <= effectCap && cleanupFee <= cleanupCap && rent - released + unresolvedRent + nextRent <= rentCap;
    }
}
