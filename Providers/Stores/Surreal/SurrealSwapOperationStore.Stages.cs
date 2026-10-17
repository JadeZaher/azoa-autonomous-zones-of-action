using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealSwapOperationStore
{
    /// <inheritdoc/>
    public Task<SwapStageRecord?> GetStageAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<SwapStageRecord>(SurrealQuery<SwapStageRecord>.Key(id), ct);

    /// <inheritdoc/>
    public async Task<SwapStageRecord?> ClaimInitialStageAsync(string operationId, long expectedVersion,
        SwapStagePlan plan, SigningAdmissionAuthority authority, CancellationToken ct = default)
    {
        var operation = await GetAsync(operationId, ct);
        if (operation is null) return null;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        if (!SamePrincipal(intent, authority) || plan.Purpose != "Effect" || plan.CleanupConsent is not null || plan.Generation != 0 || string.IsNullOrWhiteSpace(plan.Stage) ||
            plan.IntentHash != operation.IntentHash || plan.PreparationKey != SwapOperationIdentity.Preparation(
                operationId, plan.Stage, plan.Generation, operation.IntentHash) ||
            string.IsNullOrEmpty(plan.UnsignedPayloadJson) || string.IsNullOrEmpty(plan.RecoveryMetadata) ||
            plan.RequiredSignerAddresses is not { Count: > 0 } ||
            plan.RequiredSignerAddresses.Any(string.IsNullOrWhiteSpace) || !WithinInitialBudget(intent, plan))
            return null;
        var stageId = SwapOperationIdentity.Stage(plan.PreparationKey);
        var planJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan));
        var existing = await GetStageAsync(stageId, ct);
        if (existing is not null)
            return existing.OperationId == operationId && existing.PlanJson == planJson ? existing : null;
        if (operation.Version != expectedVersion || operation.State != "Admitted" || operation.CurrentStageId is not null)
            return null;
        var guards = Authority(operation, intent.Terms, authority, false);
        if (guards is null) return null;
        var now = DateTimeOffset.UtcNow;
        var stage = new SwapStageRecord
        {
            Id = stageId, OperationId = operationId, IntentHash = operation.IntentHash, Stage = plan.Stage,
            Generation = plan.Generation, PreparationKey = plan.PreparationKey, PlanJson = planJson,
            PlanHash = CanonicalJson.HashCanonical(planJson), CreatedAt = now, UpdatedAt = now,
        };
        var operationMutation = SurrealWriter.UpdateOnly<SwapOperationRecord>(operationId)
            .Where(row => row.Version == expectedVersion && row.State == "Admitted" && row.CurrentStageId == null)
            .Set(row => row.CurrentStageId, stageId).Set(row => row.State, "Preparing")
            .Set(row => row.Version, expectedVersion + 1).Set(row => row.UpdatedAt, now).Build();
        var saved = await CommitStageAsync(guards, operationMutation, SurrealWriter.Create(stage), ct);
        if (saved is not null) return saved;
        var winner = await GetStageAsync(stageId, ct);
        return winner?.OperationId == operationId && winner.PlanJson == planJson ? winner : null;
    }

    /// <inheritdoc/>
    public async Task<SwapStageRecord?> SaveSignedStageAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, PreparedSwapStage prepared, CancellationToken ct = default)
    {
        var operation = await GetAsync(operationId, ct);
        var stage = await GetStageAsync(stageId, ct);
        if (operation is null || stage is null || stage.OperationId != operationId ||
            operation.CurrentStageId != stageId || operation.IntentHash != stage.IntentHash ||
            prepared.Plan is null || string.IsNullOrEmpty(prepared.SignedPayloadJson) ||
            prepared.TransactionIds is not { Count: > 0 } || prepared.TransactionIds.Any(string.IsNullOrWhiteSpace) ||
            CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(prepared.Plan)) != stage.PlanJson)
            return null;
        var preparedJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(prepared));
        if (stage.PreparedJson is not null)
            return stage.PreparedJson == preparedJson ? stage : null;
        if (operation.Version != expectedOperationVersion || operation.State != "Preparing" ||
            stage.Version != expectedStageVersion || stage.State != "Planned")
            return null;
        var now = DateTimeOffset.UtcNow;
        var operationMutation = SurrealWriter.UpdateOnly<SwapOperationRecord>(operationId)
            .Where(row => row.Version == expectedOperationVersion && row.State == "Preparing" && row.CurrentStageId == stageId)
            .Set(row => row.Version, expectedOperationVersion + 1).Set(row => row.UpdatedAt, now).Build();
        var stageMutation = SurrealWriter.UpdateOnly<SwapStageRecord>(stageId)
            .Where(row => row.Version == expectedStageVersion && row.State == "Planned" && row.PreparedJson == null)
            .Set(row => row.PreparedJson, preparedJson).Set(row => row.PreparedHash, CanonicalJson.HashCanonical(preparedJson))
            .Set(row => row.State, "Prepared").Set(row => row.Version, expectedStageVersion + 1)
            .Set(row => row.UpdatedAt, now).Build();
        return await CommitStageAsync([], operationMutation, stageMutation, ct);
    }

    /// <inheritdoc/>
    public Task<SwapStageRecord?> AuthorizeStageAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, SigningAdmissionAuthority authority, CancellationToken ct = default)
        => ChangeStageAuthorityAsync(operationId, expectedOperationVersion, stageId, expectedStageVersion, new(authority, null), false, ct);

    /// <inheritdoc/>
    public Task<SwapStageRecord?> CancelUnsentStageAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, SigningConsentWitness authority, CancellationToken ct = default)
        => ChangeStageAuthorityAsync(operationId, expectedOperationVersion, stageId, expectedStageVersion, new(null, authority), true, ct);

    private async Task<SwapStageRecord?> ChangeStageAuthorityAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, StoreAuthority authority, bool cancel, CancellationToken ct)
    {
        var operation = await GetAsync(operationId, ct);
        var stage = await GetStageAsync(stageId, ct);
        if (operation is null || stage is null || stage.OperationId != operationId || operation.CurrentStageId != stageId ||
            operation.IntentHash != stage.IntentHash) return null;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var plan = JsonSerializer.Deserialize<SwapStagePlan>(stage.PlanJson)!;
        if (cancel ? !SamePrincipal(intent, authority, true) : authority.Signing is null || !StagePrincipal(intent, plan, authority.Signing)) return null;
        if (!cancel && stage.ServerSubmissionWithdrawnAt is not null) return null;
        // Cancelling an unsent cleanup keeps CleanupReady only when an earlier stage had egress to clean up; a never-sent
        // operation returns to Cancelled so its allowance lane can be handed over (see Services/Dex/AGENTS.md §Cleanup semantics).
        var cancelledState = cancel && plan.Purpose == "Cleanup" && operation.CleanupConsentJson is not null &&
            await HadEgressAsync(operationId, ct) ? "CleanupReady" : "Cancelled";
        if (cancel && operation.State == cancelledState && stage.State == "Cancelled") return stage;
        if (!cancel && operation.State == "Pending" && stage.State == "SubmissionAuthorized") return stage;
        var exposed = stage.ExternalExposedAt is not null;
        if (operation.Version != expectedOperationVersion ||
            (exposed ? cancel || operation.State is not ("Pending" or "NeedsOperator") : operation.State != "Preparing") ||
            stage.Version != expectedStageVersion ||
            (cancel ? stage.State is not ("Planned" or "Prepared") :
                exposed ? stage.State is not ("ExternalExposed" or "NeedsOperator") : stage.State != "Prepared") ||
            (!cancel && (stage.PreparedJson is null || stage.PreparedHash is null))) return null;
        var guards = cancel ? Authority(operation, intent.Terms, authority, true)
            : StageAuthority(operation, intent, plan, authority.Signing!);
        if (guards is null) return null;
        var now = DateTimeOffset.UtcNow;
        var previousOperationState = operation.State;
        var operationMutation = SurrealWriter.UpdateOnly<SwapOperationRecord>(operationId)
            .Where(row => row.Version == expectedOperationVersion && row.State == previousOperationState && row.CurrentStageId == stageId)
            .Set(row => row.State, cancel ? cancelledState : "Pending").Set(row => row.Version, expectedOperationVersion + 1)
            .Set(row => row.UpdatedAt, now).Build();
        var previousStageState = stage.State;
        var stageMutation = SurrealWriter.UpdateOnly<SwapStageRecord>(stageId)
            .Where(row => row.Version == expectedStageVersion && row.State == previousStageState && row.AuthorizedAt == null && row.ServerSubmissionWithdrawnAt == null)
            .Set(row => row.State, cancel ? "Cancelled" : "SubmissionAuthorized")
            .Set(row => row.Version, expectedStageVersion + 1).Set(row => row.UpdatedAt, now);
        if (!cancel) stageMutation = stageMutation.Set(row => row.AuthorizedAt, now);
        return await CommitStageAsync(guards, operationMutation, stageMutation.Build(), ct);
    }

    /// <summary>True when any stage of the operation was ever authorized for server send or disclosed for external signing.</summary>
    private async Task<bool> HadEgressAsync(string operationId, CancellationToken ct)
        => (await executor.QueryAsync<SwapStageRecord>(SurrealQuery<SwapStageRecord>.From()
            .Where(row => row.OperationId == operationId && (row.AuthorizedAt != null || row.ExternalExposedAt != null)).Limit(1), ct)).Count != 0;

    private async Task<SwapStageRecord?> CommitStageAsync(IReadOnlyList<SurrealQuery> guards,
        SurrealQuery operationMutation, SurrealQuery stageMutation, CancellationToken ct)
    {
        var operation = SurrealGeneratedStatementScope.Assign(operationMutation, "swap_operation", "_swap_operation_saved");
        var stage = SurrealGeneratedStatementScope.Assign(stageMutation, "swap_stage", "_swap_stage_saved");
        // raw: current authority, operation epoch and stage transition commit together or all roll back.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        statements.Add(operation);
        statements.Add(SurrealQuery.Of("IF $_swap_operation_saved = NONE OR $_swap_operation_saved = [] { THROW 'SWAP_ADMISSION_CONFLICT' }"));
        statements.Add(stage);
        statements.Add(SurrealQuery.Of("IF $_swap_stage_saved = NONE OR $_swap_stage_saved = [] { THROW 'SWAP_ADMISSION_CONFLICT' }"));
        statements.Add(SurrealQuery.Of("RETURN $_swap_stage_saved"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            var result = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct);
                response.EnsureAllOk();
                return response;
            }, ct);
            return result.GetValues<SwapStageRecord>(guards.Count + 5).Single();
        }
        catch (SurrealStatementException exception) when (ExpectedConflict(exception))
        {
            return null;
        }
    }

    private static bool SamePrincipal(SwapIntent intent, SigningAdmissionAuthority authority)
        => SamePrincipal(intent, new StoreAuthority(authority, null), false);

    private static bool SamePrincipal(SwapIntent intent, StoreAuthority authority, bool cancel)
        => authority.Credential.ActorUserId == intent.Terms.ActorUserId && authority.Credential.TenantId != Guid.Empty &&
            (authority.Credential.TenantId == intent.Terms.TenantId || (cancel && authority.Credential.TenantId is null));

    private static bool WithinInitialBudget(SwapIntent intent, SwapStagePlan plan)
        => UnsignedIntegerText.TryParse(plan.ReservedNetworkFee, out var fee) &&
            UnsignedIntegerText.TryParse(intent.Terms.Budget.MaximumNetworkFee, out var feeCap) && fee <= feeCap &&
            UnsignedIntegerText.TryParse(plan.ReservedLockedBalance, out var rent) &&
            UnsignedIntegerText.TryParse(intent.Terms.Budget.MaximumLockedBalance, out var rentCap) && rent <= rentCap;
}
