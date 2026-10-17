using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Actions.Workflow;
using AZOA.WebAPI.Sagas;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using ExecutionRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionNodeExecution;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Atomically audits and wakes one version-fenced parked action operation.</summary>
public sealed class SurrealActionRecoveryStore(ISurrealExecutor executor)
{
    private const string Conflict = "ACTION_OPERATOR_RECOVERY_CHANGED";

    public Task<ActionRecoveryAudit?> GetAuditAsync(string id, CancellationToken ct)
        => executor.QuerySingleAsync<ActionRecoveryAudit>(SurrealQuery<ActionRecoveryAudit>.Key(id), ct);

    public Task<SagaSteps?> GetStepAsync(Guid runId, Guid nodeId, CancellationToken ct)
        => executor.QuerySingleAsync<SagaSteps>(SurrealQuery<SagaSteps>.Key(ActionWorkflowIdentity.StepId(runId, nodeId)), ct);

    public Task<IReadOnlyList<ExecutionRow>> ListAsync(int limit, CancellationToken ct)
        => executor.QueryAsync<ExecutionRow>(SurrealQuery<ExecutionRow>.From().Where(row => row.RecoveryState == "NeedsOperator")
            .OrderBy(row => row.StartedAt).ThenBy(row => row.Id).Limit(Math.Clamp(limit, 1, 100)), ct);

    public Task<IReadOnlyList<ActionRecoveryAudit>> HistoryAsync(string executionId, CancellationToken ct)
        => executor.QueryAsync<ActionRecoveryAudit>(SurrealQuery<ActionRecoveryAudit>.From().Where(row => row.ExecutionId == executionId)
            .OrderByDescending(row => row.CreatedAt).Limit(50), ct);

    public async Task<AZOAResult<ActionRecoveryAudit>> RetryAsync(ActionRecoveryAudit audit, string actorId, string? tenantId,
        string frozenInput, string operationJson, string payload, string admissionPayloadHash, CancellationToken ct)
    {
        var previous = await GetAuditAsync(audit.Id, ct);
        if (previous is not null) return Replay(previous, audit);
        var run = Guid.ParseExact(audit.RunId, "N");
        var node = Guid.ParseExact(audit.NodeId, "N");
        var sagaId = ActionWorkflowIdentity.StepId(run, node);
        // raw: original authority, exact parked generation, wake and immutable operator audit commit together.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(ActionExecutionFence.Create(audit.RunId, audit.NodeId, actorId, tenantId, audit.IntentHash));
        statements.Add(SurrealQuery.Of("LET $_recovery_node = (SELECT * FROM ONLY type::record('action_node_execution', $_recovery_id))")
            .WithParam("_recovery_id", audit.ExecutionId));
        statements.Add(SurrealQuery.Of("""
            IF $_recovery_node = NONE OR $_recovery_node.version != $_recovery_version
                OR $_recovery_node.recovery_state != 'NeedsOperator' OR $_recovery_node.state != 'Running'
                OR $_recovery_node.frozen_input != $_recovery_input OR $_recovery_node.external_operation_json != $_recovery_operation
                OR $_recovery_node.run_id != type::record('action_run', $_recovery_run)
                OR $_recovery_node.node_id != type::record('action_node', $_recovery_node_id)
                { THROW 'ACTION_OPERATOR_RECOVERY_CHANGED' }
            """).WithParam("_recovery_version", audit.ExpectedVersion).WithParam("_recovery_input", frozenInput)
            .WithParam("_recovery_operation", operationJson).WithParam("_recovery_run", audit.RunId).WithParam("_recovery_node_id", audit.NodeId));
        statements.Add(SurrealQuery.Of("LET $_recovery_step = (SELECT * FROM ONLY type::record('saga_steps', $_recovery_step_id))")
            .WithParam("_recovery_step_id", sagaId));
        statements.Add(SurrealQuery.Of("""
            IF $_recovery_step = NONE OR $_recovery_step.saga_name != $_recovery_saga
                OR $_recovery_step.correlation_key != $_recovery_correlation OR $_recovery_step.step_name != $_recovery_step_name
                OR $_recovery_step.status != 'Parked' OR $_recovery_step.is_compensation OR $_recovery_step.gate_id != $_recovery_gate
                OR $_recovery_step.step_idempotency_key != $_recovery_step_key
                OR $_recovery_step.payload != $_recovery_payload OR $_recovery_step.admission_payload_hash != $_recovery_payload_hash
                { THROW 'ACTION_OPERATOR_RECOVERY_CHANGED' }
            """).WithParam("_recovery_saga", ActionWorkflowSaga.Name).WithParam("_recovery_correlation", SagaOpaqueKey.Encode(run.ToString()))
            .WithParam("_recovery_step_name", SagaOpaqueKey.Encode(node.ToString())).WithParam("_recovery_gate", SagaOpaqueKey.Encode($"operator:{node:N}"))
            .WithParam("_recovery_step_key", SagaOpaqueKey.Encode(SagaKeys.StepIdempotencyKey(run.ToString(), node.ToString())))
            .WithParam("_recovery_payload", payload).WithParam("_recovery_payload_hash", admissionPayloadHash));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.UpdateOnly<ExecutionRow>(audit.ExecutionId)
            .Where(row => row.Version == audit.ExpectedVersion && row.State == ExecutionRow.ActionNodeState.Running && row.RecoveryState == "NeedsOperator")
            .Set(row => row.Version, audit.ExpectedVersion + 1).Set(row => row.RecoveryState, "OperatorRetryRequested").Build(), "operator_node"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.UpdateOnly<SagaSteps>(sagaId)
            .Where(row => row.Status == SagaSteps.StepStatus.Parked)
            .Set(row => row.Status, SagaSteps.StepStatus.Pending).Set(row => row.NextRunAt, audit.CreatedAt)
            .Set(row => row.UpdatedAt, audit.CreatedAt).Unset(row => row.GateId).Unset(row => row.ClaimedAt).Build(), "operator_step"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(audit), "operator_audit"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(SurrealQuery.Combine(statements.ToArray()), ct);
                response.EnsureAllOk();
                return response;
            }, ct);
            return AZOAResult<ActionRecoveryAudit>.Success(audit);
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || exception.Message.Contains(ActionExecutionFence.FailureMarker, StringComparison.Ordinal)
            || SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var saved = await GetAuditAsync(audit.Id, ct);
            return saved is null ? AZOAResult<ActionRecoveryAudit>.Failure("The parked action changed; inspect its current state before retrying.") : Replay(saved, audit);
        }
    }

    private static AZOAResult<ActionRecoveryAudit> Replay(ActionRecoveryAudit saved, ActionRecoveryAudit expected)
        => saved.OperatorId == expected.OperatorId && saved.RequestHash == expected.RequestHash && saved.ExecutionId == expected.ExecutionId
            ? AZOAResult<ActionRecoveryAudit>.Success(saved)
            : AZOAResult<ActionRecoveryAudit>.Failure("Recovery request key belongs to another immutable request.");
}
