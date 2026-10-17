using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Dex;

/// <summary>Coordinates immutable stage custody, browser acceptance and already-authorized recovery.</summary>
public sealed partial class SwapStageCoordinator(ISwapOperationStore store)
{
    public async Task<AZOAResult<SwapOperationRecord>> ProgressAsync(string operationId, long expectedVersion,
        SigningAdmissionAuthority authority, IDexTransactionModule module, CancellationToken ct = default)
    {
        var operation = await store.GetAsync(operationId, ct);
        if (operation is null || operation.Version != expectedVersion || operation.State is not ("Admitted" or "StageResolved"))
            return AZOAResult<SwapOperationRecord>.Failure("Reload the current swap before choosing another stage.");
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        if (intent.Terms.ActorUserId != authority.Credential.ActorUserId || intent.Terms.TenantId != authority.Credential.TenantId ||
            intent.Terms.Network.ChainType != module.ChainType || intent.Terms.Network.Network != module.ActiveNetwork.ToString() ||
            intent.Terms.Provider != module.DexCapability.Provider || intent.Terms.ProtocolVersion != module.DexCapability.ProtocolVersion ||
            CanonicalJson.HashCanonical(operation.IntentJson) != operation.IntentHash)
            return AZOAResult<SwapOperationRecord>.Failure("The original swap participant or provider differs.");
        var records = await ReadHistoryRecordsAsync(operation, ct);
        var history = records is null ? null : ValidatedHistory(operation, records, module, intent, false);
        if (records is null || history is null) return AZOAResult<SwapOperationRecord>.Failure("The saved swap history is incomplete or unresolved.");
        var next = await module.GetNextSwapStageAsync(intent, history, ct);
        if (next.IsError || next.Result is null) return AZOAResult<SwapOperationRecord>.Failure(next.Message);
        if (next.Result.Complete)
        {
            if (next.Result.Stage is not null || records.Count == 0)
                return AZOAResult<SwapOperationRecord>.Failure("Completion requires an exact finalized economic stage.");
            var completed = await store.CompleteAsync(operationId, expectedVersion, SurrealRecordGuid.BareId(records[0].Id),
                records[0].Version, next.Result.RetainedEffects, ct);
            return completed is null ? AZOAResult<SwapOperationRecord>.Failure("The swap changed before completion.")
                : AZOAResult<SwapOperationRecord>.Success(completed);
        }
        if (string.IsNullOrWhiteSpace(next.Result.Stage)) return AZOAResult<SwapOperationRecord>.Failure(next.Result.Detail ?? "No next stage is available.");
        var generation = records.Count > 0 && records[0].Stage == next.Result.Stage ? checked(records[0].Generation + 1) : 0;
        var planned = await module.PlanSwapStageAsync(intent, next.Result.Stage, generation, ct);
        if (planned.IsError || planned.Result is null) return AZOAResult<SwapOperationRecord>.Failure(planned.Message);
        var claimed = operation.CurrentStageId is null
            ? await store.ClaimInitialStageAsync(operationId, expectedVersion, planned.Result, authority, ct)
            : await store.ClaimNextStageAsync(operationId, expectedVersion, planned.Result, authority, ct);
        if (claimed is null) return AZOAResult<SwapOperationRecord>.Failure("The swap or current signing authority changed before planning.");
        return AZOAResult<SwapOperationRecord>.Success((await store.GetAsync(operationId, ct))!);
    }

    public async Task<AZOAResult<SwapStageRecord>> PlanCleanupAsync(string operationId, long expectedVersion,
        SigningAdmissionAuthority authority, IDexTransactionModule module, string stage, CancellationToken ct = default)
    {
        var operation = await store.GetAsync(operationId, ct);
        if (operation is null || operation.Version != expectedVersion || operation.CleanupConsentJson is null ||
            operation.State is not ("CleanupReady" or "StageResolved")) return Refused();
        var consent = JsonSerializer.Deserialize<SwapCleanupConsent>(operation.CleanupConsentJson)!;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        if (!SwapCleanupContract.IsValid(intent, consent) || consent.ActorUserId != authority.Credential.ActorUserId ||
            consent.TenantId != authority.Credential.TenantId || authority.Kind == "Workflow" || authority.Action is not null ||
            intent.Terms.Network.ChainType != module.ChainType || intent.Terms.Network.Network != module.ActiveNetwork.ToString() ||
            intent.Terms.Provider != module.DexCapability.Provider || intent.Terms.ProtocolVersion != module.DexCapability.ProtocolVersion)
            return Refused();
        var records = await ReadHistoryRecordsAsync(operation, ct);
        var history = records is null ? null : ValidatedHistory(operation, records, module, intent, true);
        if (records is null || records.Count == 0 || history is null) return Refused();
        var generation = records[0].Stage == stage ? checked(records[0].Generation + 1) : 0;
        var planned = await module.PlanSwapCleanupAsync(intent, history, consent, stage, generation, ct);
        if (planned.IsError || planned.Result is null) return AZOAResult<SwapStageRecord>.Failure(planned.Message);
        if (planned.Result.CleanupConsent != consent || planned.Result.Purpose != "Cleanup") return Refused();
        var saved = await store.ClaimNextStageAsync(operationId, expectedVersion, planned.Result, authority, ct);
        return saved is null ? Refused() : AZOAResult<SwapStageRecord>.Success(saved);
    }

    public async Task<AZOAResult<SwapStageRecord>> SignAsync(string operationId, long operationVersion,
        string stageId, long stageVersion, SigningAdmissionAuthority authority, IDexTransactionModule module,
        CancellationToken ct = default)
    {
        var operation = await store.GetAsync(operationId, ct);
        var stage = await store.GetStageAsync(stageId, ct);
        if (!TryRead(operation, stage, module, out var intent, out var plan)) return Refused();
        var admitted = await store.AdmitCustodyAsync(operationId, operationVersion, stageId, stageVersion, authority, ct);
        if (admitted is null) return Refused();
        var signingTenant = plan.CleanupConsent?.TenantId ?? (plan.Purpose == "Cleanup" ? null : intent.Terms.TenantId);
        var context = new SigningContext(intent.Terms.ActorUserId, intent.Terms.Owner.WalletId, false,
            signingTenant ?? Guid.Empty, intent.Terms.ActorUserId, AzoaScopes.SwapSign, intent.Terms.Network.ChainType);
        var signed = await module.SignSwapStageAsync(intent, plan, [new(intent.Terms.Owner, context)], ct);
        if (signed.IsError || signed.Result is null)
            return AZOAResult<SwapStageRecord>.Failure(signed.Message);
        var validated = module.ValidateSignedSwapStage(intent, plan, signed.Result.SignedPayloadJson);
        if (validated.IsError || validated.Result is null ||
            !validated.Result.TransactionIds.SequenceEqual(signed.Result.TransactionIds)) return Refused();
        var saved = await store.SaveSignedStageAsync(operationId, checked(operationVersion + 1), stageId,
            admitted.Version, validated.Result, ct);
        return saved is null ? Refused() : AZOAResult<SwapStageRecord>.Success(saved);
    }

    public async Task<AZOAResult<SwapStageRecord>> AcceptBrowserAsync(string operationId, long operationVersion,
        string stageId, long stageVersion, string signedPayloadJson, SigningAdmissionAuthority authority,
        IDexTransactionModule module, CancellationToken ct = default)
    {
        var operation = await store.GetAsync(operationId, ct);
        var stage = await store.GetStageAsync(stageId, ct);
        if (!TryRead(operation, stage, module, out var intent, out var plan)) return Refused();
        var validated = module.ValidateSignedSwapStage(intent, plan, signedPayloadJson);
        if (validated.IsError || validated.Result is null) return AZOAResult<SwapStageRecord>.Failure(validated.Message);
        var tenant = plan.Purpose == "Cleanup" ? plan.CleanupConsent?.TenantId : intent.Terms.TenantId;
        if (stage!.ExternalExposedAt is null || authority.Credential.ActorUserId != intent.Terms.ActorUserId ||
            authority.Credential.TenantId != tenant || (plan.Purpose == "Cleanup" && authority.Kind == "Workflow")) return Refused();
        var saved = await store.SaveExposedCandidateAsync(operationId, operationVersion, stageId,
            stageVersion, validated.Result, ct);
        return saved is null ? Refused() : AZOAResult<SwapStageRecord>.Success(saved);
    }

    public async Task<SwapStageRecord?> RecoverAuthorizedAsync(string operationId, IDexTransactionModule module,
        CancellationToken ct = default)
    {
        var operation = await store.GetAsync(operationId, ct);
        if (operation?.CurrentStageId is not { } stageId) return null;
        var stage = await store.GetStageAsync(stageId, ct);
        if (stage?.ExternalExposedAt is not null && stage.AuthorizedAt is null)
            return await RecoverExposedAsync(operationId, module, ct);
        if (!TryRead(operation, stage, module, out var intent, out var plan) ||
            operation.State is not ("Pending" or "NeedsOperator") || stage!.State is not ("SubmissionAuthorized" or "NeedsOperator") ||
            stage.AuthorizedAt is null || !TryPrepared(stage, plan, module, intent, out var prepared)) return stage;
        var observed = await module.ObserveSwapStageAsync(intent, prepared, ct, ReadSavedObservation(stage));
        if (observed.IsError || observed.Result is null || !ValidFinalizedCandidate(module, intent, prepared, observed.Result)) return stage;
        var saved = await store.RecordObservationAsync(operationId, operation.Version, stageId, stage.Version, observed.Result, ct);
        if (saved is null || observed.Result.Verdict != "Pending") return saved;
        var current = await store.GetAsync(operationId, ct);
        var currentStage = await store.GetStageAsync(stageId, ct);
        if (current?.State != "Pending" || current.CurrentStageId != stageId ||
            currentStage?.State != "SubmissionAuthorized" || currentStage.AuthorizedAt is null ||
            currentStage.ServerSubmissionWithdrawnAt is not null ||
            currentStage.PreparedHash != stage.PreparedHash) return currentStage;
        await module.SubmitSwapStageAsync(intent, prepared, ct);
        return currentStage;
    }

    public async Task<SwapStageRecord?> ResolveAccountingAsync(string operationId, string stageId,
        IDexTransactionModule module, CancellationToken ct = default)
    {
        var operation = await store.GetAsync(operationId, ct);
        var stage = await store.GetStageAsync(stageId, ct);
        if (!TryRead(operation, stage, module, out var intent, out var plan) || stage!.State != "Confirmed" ||
            (stage.AuthorizedAt is null && stage.ExternalExposedAt is null) || stage.ResolvedLockedBalance is not null || stage.ObservationJson is null ||
            !TryPrepared(stage, plan, module, intent, out var prepared)) return stage;
        var original = JsonSerializer.Deserialize<SwapStageObservation>(stage.ObservationJson);
        if (original is null || original.LockedBalance is not null || !SwapObservationContract.IsValid(intent, prepared, original)) return stage;
        var proof = await module.ResolveSwapStageAccountingAsync(intent, prepared, ct);
        if (proof.IsError || proof.Result is null) return stage;
        return await store.ResolveRentAccountingAsync(operationId, operation!.Version, stageId, stage.Version,
            proof.Result.ActualLockedBalance, proof.Result.EvidenceJson, ct);
    }

    public async Task<SwapOperationRecord?> FinalizeObservedAsync(string operationId, IDexTransactionModule module,
        CancellationToken ct = default)
    {
        var operation = await store.GetAsync(operationId, ct);
        if (operation?.State != "StageResolved" || operation.CleanupStarted) return operation;
        var records = await ReadHistoryRecordsAsync(operation, ct);
        if (records is null || records.Count == 0 || !TryRead(operation, records[0], module, out var intent, out _)) return operation;
        var history = ValidatedHistory(operation, records, module, intent, false);
        if (history is null) return operation;
        var next = await module.GetNextSwapStageAsync(intent, history, ct);
        if (next.IsError || next.Result is not { Complete: true, Stage: null } complete) return operation;
        return await store.CompleteAsync(operationId, operation.Version, SurrealRecordGuid.BareId(records[0].Id),
            records[0].Version, complete.RetainedEffects, ct) ?? operation;
    }

    public async Task<SwapStageRecord?> ResolveRentReleaseAsync(string operationId, string allocationStageId,
        string cleanupStageId, IDexTransactionModule module, CancellationToken ct = default)
    {
        var operation = await store.GetAsync(operationId, ct);
        var allocation = await store.GetStageAsync(allocationStageId, ct);
        var cleanup = await store.GetStageAsync(cleanupStageId, ct);
        if (!TryRead(operation, allocation, module, out var intent, out var allocationPlan) ||
            !TryRead(operation, cleanup, module, out _, out var cleanupPlan) || allocation!.State != "Confirmed" ||
            cleanup!.State != "Confirmed" || (allocation.AuthorizedAt is null && allocation.ExternalExposedAt is null) ||
            (cleanup.AuthorizedAt is null && cleanup.ExternalExposedAt is null) ||
            allocation.ReleasedLockedBalance is not null ||
            !TryPrepared(allocation, allocationPlan, module, intent, out var preparedAllocation) ||
            !TryPrepared(cleanup, cleanupPlan, module, intent, out var preparedCleanup)) return allocation;
        var proof = await module.ScanSwapRentReleaseAsync(intent, preparedAllocation, preparedCleanup, allocation.RentReleaseScanJson, ct);
        if (proof.IsError || proof.Result is null) return allocation;
        return await store.RecordRentReleaseAsync(operationId, operation!.Version, allocationStageId, allocation.Version,
            cleanupStageId, cleanup.Version, proof.Result, ct);
    }

    private static bool TryRead(SwapOperationRecord? operation, SwapStageRecord? stage, IDexTransactionModule module,
        out SwapIntent intent, out SwapStagePlan plan)
    {
        intent = null!;
        plan = null!;
        if (operation is null || stage is null || stage.OperationId != SurrealRecordGuid.BareId(operation.Id) ||
            stage.IntentHash != operation.IntentHash || CanonicalJson.HashCanonical(operation.IntentJson) != operation.IntentHash ||
            CanonicalJson.HashCanonical(stage.PlanJson) != stage.PlanHash) return false;
        intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        plan = JsonSerializer.Deserialize<SwapStagePlan>(stage.PlanJson)!;
        return intent is not null && plan is not null && intent.OperationId == stage.OperationId && plan.IntentHash == operation.IntentHash &&
            intent.Terms.Network.ChainType == module.ChainType && intent.Terms.Network.Network == module.ActiveNetwork.ToString() &&
            intent.Terms.Provider == module.DexCapability.Provider && intent.Terms.ProtocolVersion == module.DexCapability.ProtocolVersion;
    }

    private async Task<List<SwapStageRecord>?> ReadHistoryRecordsAsync(SwapOperationRecord operation, CancellationToken ct)
    {
        var records = new List<SwapStageRecord>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var cursor = operation.CurrentStageId;
        while (cursor is not null)
        {
            ct.ThrowIfCancellationRequested();
            var page = await store.ReadHistoryPageAsync(SurrealRecordGuid.BareId(operation.Id), cursor, ct);
            if (page.Count == 0) return null;
            foreach (var record in page)
            {
                if (cursor is null || SurrealRecordGuid.BareId(record.Id) != cursor || !visited.Add(cursor)) return null;
                records.Add(record);
                cursor = record.PreviousStageId;
            }
        }
        return records;
    }

    private static IReadOnlyList<SwapStageHistory>? ValidatedHistory(SwapOperationRecord operation,
        IReadOnlyList<SwapStageRecord> records, IDexTransactionModule module, SwapIntent intent, bool allowUnsentCancellation)
    {
        var history = new List<SwapStageHistory>();
        foreach (var record in records.Reverse())
        {
            if (!TryRead(operation, record, module, out _, out var plan)) return null;
            if (allowUnsentCancellation && record.State == "Cancelled" && record.AuthorizedAt is null && record.ExternalExposedAt is null) continue;
            if ((record.AuthorizedAt is null && record.ExternalExposedAt is null) || record.ObservationJson is null) return null;
            var observation = JsonSerializer.Deserialize<SwapStageObservation>(record.ObservationJson)!;
            if (observation.Verdict != record.State || !SwapObservationContract.IsTerminal(record.State)) return null;
            if (record.PreparedJson is null)
            {
                if (record.ExternalExposedAt is null || !module.ValidateExposedSwapNoEffect(intent, plan, observation)) return null;
                history.Add(new(null, observation, ExposedPlan: plan));
                continue;
            }
            if (!TryPrepared(record, plan, module, intent, out var prepared) ||
                !SwapObservationContract.IsValid(intent, prepared, observation) ||
                !ValidFinalizedCandidate(module, intent, prepared, observation)) return null;
            history.Add(new(prepared, observation, record.ResolvedLockedBalance is { } actual && record.RentAccountingEvidenceJson is { } evidence
                ? new(actual, evidence) : null));
        }
        return history;
    }

    private static bool TryPrepared(SwapStageRecord stage, SwapStagePlan plan, IDexTransactionModule module,
        SwapIntent intent, out PreparedSwapStage prepared)
    {
        prepared = null!;
        if (stage.PreparedJson is null || stage.PreparedHash != CanonicalJson.HashCanonical(stage.PreparedJson)) return false;
        var saved = JsonSerializer.Deserialize<PreparedSwapStage>(stage.PreparedJson);
        if (saved is null || CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(saved.Plan)) != stage.PlanJson) return false;
        var validated = module.ValidateSignedSwapStage(intent, plan, saved.SignedPayloadJson);
        if (validated.IsError || validated.Result is null || !validated.Result.TransactionIds.SequenceEqual(saved.TransactionIds) ||
            validated.Result.SignedPayloadJson != saved.SignedPayloadJson) return false;
        prepared = saved;
        return true;
    }

    private static bool ValidFinalizedCandidate(IDexTransactionModule module, SwapIntent intent,
        PreparedSwapStage original, SwapStageObservation observation)
    {
        if (observation.FinalizedCandidate is not { } winner) return true;
        if (!SwapObservationContract.IsValid(intent, original, observation)) return false;
        var validated = module.ValidateSignedSwapStage(intent, original.Plan, winner.Candidate.SignedPayloadJson);
        return validated.Result is { } actual && !validated.IsError &&
            actual.SignedPayloadJson == winner.Candidate.SignedPayloadJson &&
            actual.TransactionIds.SequenceEqual(winner.Candidate.TransactionIds);
    }

    private static AZOAResult<SwapStageRecord> Refused()
        => AZOAResult<SwapStageRecord>.FailureWithCode("The original stage or current signing authority changed; reload before retrying.", "Conflict");
}
