using System.Text.Json;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealSwapOperationStore
{
    /// <inheritdoc/>
    public async Task<SwapStageRecord?> WithdrawSubmissionAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, SigningConsentWitness authority, CancellationToken ct = default)
    {
        var operation = await GetAsync(operationId, ct);
        var stage = await GetStageAsync(stageId, ct);
        if (operation is null || stage is null || operation.CurrentStageId != stageId || stage.OperationId != operationId ||
            stage.IntentHash != operation.IntentHash) return null;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var consent = new StoreAuthority(null, authority);
        if (!SamePrincipal(intent, consent, true)) return null;
        if (stage.ServerSubmissionWithdrawnAt is not null) return stage;
        if (operation.Version != expectedOperationVersion || stage.Version != expectedStageVersion ||
            operation.State is not ("Pending" or "NeedsOperator") ||
            stage.State is not ("SubmissionAuthorized" or "ExternalExposed" or "NeedsOperator") ||
            (stage.AuthorizedAt is null && stage.ExternalExposedAt is null)) return null;
        var guards = Authority(operation, intent.Terms, consent, true);
        if (guards is null) return null;
        var now = DateTimeOffset.UtcNow;
        var operationState = operation.State;
        var stageState = stage.State;
        var operationMutation = SurrealWriter.UpdateOnly<SwapOperationRecord>(operationId)
            .Where(row => row.Version == expectedOperationVersion && row.State == operationState && row.CurrentStageId == stageId)
            .Set(row => row.Version, expectedOperationVersion + 1).Set(row => row.UpdatedAt, now).Build();
        var stageMutation = SurrealWriter.UpdateOnly<SwapStageRecord>(stageId)
            .Where(row => row.Version == expectedStageVersion && row.State == stageState && row.ServerSubmissionWithdrawnAt == null)
            .Set(row => row.ServerSubmissionWithdrawnAt, now).Set(row => row.Version, expectedStageVersion + 1)
            .Set(row => row.UpdatedAt, now).Build();
        return await CommitStageAsync(guards, operationMutation, stageMutation, ct);
    }
}
