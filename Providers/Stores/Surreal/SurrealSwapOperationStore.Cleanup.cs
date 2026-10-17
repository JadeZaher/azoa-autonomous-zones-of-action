using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealSwapOperationStore
{
    private static IReadOnlyList<SurrealQuery>? StageAuthority(SwapOperationRecord operation, SwapIntent intent,
        SwapStagePlan plan, SigningAdmissionAuthority authority)
    {
        if (plan.Purpose == "Effect")
            return plan.CleanupConsent is null ? Authority(operation, intent.Terms, authority, false) : null;
        if (plan.Purpose != "Cleanup" || plan.CleanupConsent is not { } consent ||
            !SwapCleanupContract.IsValid(intent, consent) || operation.CleanupConsentJson !=
                CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(consent))) return null;
        return DexSigningAuthorityFence.Cleanup(intent.Terms, consent, authority);
    }

    private static bool StagePrincipal(SwapIntent intent, SwapStagePlan plan, SigningAdmissionAuthority authority)
        => plan.Purpose == "Effect" ? SamePrincipal(intent, authority) : plan.Purpose == "Cleanup" &&
            plan.CleanupConsent is { } consent && consent.ActorUserId == authority.Credential.ActorUserId &&
            consent.TenantId == authority.Credential.TenantId && authority.Kind != "Workflow" && authority.Action is null;

    /// <inheritdoc/>
    public async Task<SwapOperationRecord?> BeginCleanupAsync(string operationId, long expectedOperationVersion,
        SwapCleanupConsent consent, SigningAdmissionAuthority authority, CancellationToken ct = default)
    {
        var operation = await GetAsync(operationId, ct);
        if (operation is null || operation.CostAccountingVersion != 1) return null;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        if (!SwapCleanupContract.IsValid(intent, consent) || authority.Credential.ActorUserId != consent.ActorUserId ||
            authority.Credential.TenantId != consent.TenantId || authority.Kind == "Workflow" || authority.Action is not null) return null;
        var consentJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(consent));
        if (operation.CleanupConsentJson is not null) return operation.CleanupConsentJson == consentJson ? operation : null;
        if (operation.Version != expectedOperationVersion || operation.CurrentStageId is null ||
            operation.State is not ("Completed" or "Cancelled" or "StageResolved")) return null;
        var guards = DexSigningAuthorityFence.Cleanup(intent.Terms, consent, authority);
        if (guards is null) return null;
        var current = await GetStageAsync(operation.CurrentStageId, ct);
        if (current is null || current.OperationId != operationId || current.IntentHash != operation.IntentHash) return null;
        var next = operation.CurrentStageId;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (next is not null)
        {
            var page = await ReadHistoryPageAsync(operationId, next, ct);
            if (page.Count == 0) return null;
            foreach (var stage in page)
            {
                if (next is null || SurrealRecordGuid.BareId(stage.Id) != next || !seen.Add(next)) return null;
                if (stage.AuthorizedAt is not null || stage.ExternalExposedAt is not null)
                {
                    if (!SwapObservationContract.IsTerminal(stage.State) || stage.ObservationJson is null) return null;
                    var observation = JsonSerializer.Deserialize<SwapStageObservation>(stage.ObservationJson)!;
                    if (observation.Verdict != stage.State || !ValidJournalObservation(intent, stage, observation)) return null;
                }
                else if (stage.State != "Cancelled") return null;
                next = stage.PreviousStageId;
            }
        }
        var now = DateTimeOffset.UtcNow;
        var previousState = operation.State;
        var stageId = operation.CurrentStageId;
        var stageState = current.State;
        var stageVersion = current.Version;
        var mutation = SurrealWriter.UpdateOnly<SwapOperationRecord>(operationId)
            .Where(row => row.Version == expectedOperationVersion && row.State == previousState &&
                row.CurrentStageId == stageId && row.CleanupConsentJson == null)
            .Set(row => row.CleanupConsentJson, consentJson).Set(row => row.State, "CleanupReady")
            .Set(row => row.Version, expectedOperationVersion + 1).Set(row => row.UpdatedAt, now).Build();
        var stageMutation = SurrealWriter.UpdateOnly<SwapStageRecord>(stageId)
            .Where(row => row.Version == stageVersion && row.State == stageState)
            .Set(row => row.Version, stageVersion + 1).Set(row => row.UpdatedAt, now).Build();
        return await CommitStageAsync(guards, mutation, stageMutation, ct) is null ? null : await GetAsync(operationId, ct);
    }
}
