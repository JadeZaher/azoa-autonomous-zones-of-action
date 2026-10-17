using System.Text.Json;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealSwapOperationStore
{
    /// <inheritdoc/>
    public async Task<SwapStageRecord?> SaveExposedCandidateAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, PreparedSwapStage prepared, CancellationToken ct = default)
    {
        var operation = await GetAsync(operationId, ct);
        var stage = await GetStageAsync(stageId, ct);
        if (operation is null || stage is null || operation.CurrentStageId != stageId || stage.OperationId != operationId ||
            operation.IntentHash != stage.IntentHash || stage.ExternalExposedAt is null ||
            prepared.Plan is null || string.IsNullOrWhiteSpace(prepared.SignedPayloadJson) ||
            prepared.TransactionIds is not { Count: > 0 and <= 8 } || prepared.TransactionIds.Any(string.IsNullOrWhiteSpace) ||
            CanonicalJson.Hash(prepared.Plan) != stage.PlanHash || stage.ExternalTransactionIdsJson is null) return null;
        var exposedIds = JsonSerializer.Deserialize<string[]>(stage.ExternalTransactionIdsJson)!;
        if (exposedIds.Length != 0 && !exposedIds.SequenceEqual(prepared.TransactionIds)) return null;
        var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(prepared));
        if (stage.PreparedJson is not null) return stage.PreparedJson == json ? stage : null;
        if (operation.Version != expectedOperationVersion || stage.Version != expectedStageVersion ||
            operation.State is not ("Pending" or "NeedsOperator") || stage.State is not ("ExternalExposed" or "NeedsOperator") ||
            stage.AuthorizedAt is not null) return null;
        var now = DateTimeOffset.UtcNow;
        var operationState = operation.State;
        var stageState = stage.State;
        var operationMutation = SurrealWriter.UpdateOnly<SwapOperationRecord>(operationId)
            .Where(row => row.Version == expectedOperationVersion && row.State == operationState && row.CurrentStageId == stageId)
            .Set(row => row.Version, expectedOperationVersion + 1).Set(row => row.UpdatedAt, now).Build();
        var stageMutation = SurrealWriter.UpdateOnly<SwapStageRecord>(stageId)
            .Where(row => row.Version == expectedStageVersion && row.State == stageState && row.PreparedJson == null && row.AuthorizedAt == null)
            .Set(row => row.PreparedJson, json).Set(row => row.PreparedHash, CanonicalJson.HashCanonical(json))
            .Set(row => row.Version, expectedStageVersion + 1).Set(row => row.UpdatedAt, now).Build();
        return await CommitStageAsync([], operationMutation, stageMutation, ct);
    }

    /// <inheritdoc/>
    public async Task<SwapStageRecord?> ExposeStageAsync(string operationId, long expectedOperationVersion, string stageId,
        long expectedStageVersion, string exposureHash, IReadOnlyList<string> transactionIds,
        SigningAdmissionAuthority authority, CancellationToken ct = default)
    {
        if (exposureHash is not { Length: 64 } || exposureHash.Any(character => !char.IsAsciiHexDigitLower(character)) ||
            transactionIds is null || transactionIds.Count > 8 || transactionIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256) ||
            transactionIds.Distinct(StringComparer.Ordinal).Count() != transactionIds.Count) return null;
        var operation = await GetAsync(operationId, ct);
        var stage = await GetStageAsync(stageId, ct);
        if (operation is null || stage is null || operation.CurrentStageId != stageId || stage.OperationId != operationId ||
            stage.IntentHash != operation.IntentHash || CanonicalJson.HashCanonical(stage.PlanJson) != stage.PlanHash) return null;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var plan = JsonSerializer.Deserialize<SwapStagePlan>(stage.PlanJson)!;
        if (!StagePrincipal(intent, plan, authority)) return null;
        var ids = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(transactionIds));
        if (stage.ExternalExposedAt is not null)
            return stage.ExternalExposureHash == exposureHash && stage.ExternalTransactionIdsJson == ids ? stage : null;
        if (operation.State != "Preparing" || stage.State != "Planned" || operation.Version != expectedOperationVersion ||
            stage.Version != expectedStageVersion || stage.PreparedJson is not null || stage.AuthorizedAt is not null) return null;
        var guards = StageAuthority(operation, intent, plan, authority);
        if (guards is null) return null;
        var now = DateTimeOffset.UtcNow;
        var operationMutation = SurrealWriter.UpdateOnly<SwapOperationRecord>(operationId)
            .Where(row => row.Version == expectedOperationVersion && row.State == "Preparing" && row.CurrentStageId == stageId)
            .Set(row => row.State, "Pending").Set(row => row.Version, expectedOperationVersion + 1)
            .Set(row => row.UpdatedAt, now).Set(row => row.NextAttemptAt, now).Build();
        var stageMutation = SurrealWriter.UpdateOnly<SwapStageRecord>(stageId)
            .Where(row => row.Version == expectedStageVersion && row.State == "Planned" && row.PreparedJson == null &&
                row.AuthorizedAt == null && row.ExternalExposedAt == null)
            .Set(row => row.State, "ExternalExposed").Set(row => row.ExternalExposedAt, now)
            .Set(row => row.ExternalExposureHash, exposureHash).Set(row => row.ExternalTransactionIdsJson, ids)
            .Set(row => row.Version, expectedStageVersion + 1).Set(row => row.UpdatedAt, now).Build();
        return await CommitStageAsync(guards, operationMutation, stageMutation, ct);
    }
}
