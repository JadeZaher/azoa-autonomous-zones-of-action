using System.Text.Json;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealSwapOperationStore
{
    /// <inheritdoc/>
    public async Task<SwapStageRecord?> AdmitCustodyAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, SigningAdmissionAuthority authority, CancellationToken ct = default)
    {
        var operation = await GetAsync(operationId, ct);
        var stage = await GetStageAsync(stageId, ct);
        if (operation is null || stage is null || operation.State != "Preparing" || stage.State != "Planned" ||
            operation.Version != expectedOperationVersion || stage.Version != expectedStageVersion ||
            operation.CurrentStageId != stageId || stage.OperationId != operationId ||
            stage.IntentHash != operation.IntentHash || stage.PreparedJson is not null || stage.AuthorizedAt is not null)
            return null;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var plan = JsonSerializer.Deserialize<SwapStagePlan>(stage.PlanJson)!;
        if (!StagePrincipal(intent, plan, authority)) return null;
        var guards = StageAuthority(operation, intent, plan, authority);
        if (guards is null) return null;
        var now = DateTimeOffset.UtcNow;
        var operationMutation = SurrealWriter.UpdateOnly<SwapOperationRecord>(operationId)
            .Where(row => row.Version == expectedOperationVersion && row.State == "Preparing" && row.CurrentStageId == stageId)
            .Set(row => row.Version, expectedOperationVersion + 1).Set(row => row.UpdatedAt, now).Build();
        var stageMutation = SurrealWriter.UpdateOnly<SwapStageRecord>(stageId)
            .Where(row => row.Version == expectedStageVersion && row.State == "Planned" && row.PreparedJson == null && row.AuthorizedAt == null)
            .Set(row => row.Version, expectedStageVersion + 1).Set(row => row.UpdatedAt, now).Build();
        return await CommitStageAsync(guards, operationMutation, stageMutation, ct);
    }
}
