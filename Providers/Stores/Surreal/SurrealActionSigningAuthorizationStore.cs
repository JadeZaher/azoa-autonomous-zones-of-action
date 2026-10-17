using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Actions.Workflow;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using Execution = AZOA.WebAPI.Models.Actions.ActionNodeExecution;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Atomically records participant consent and wakes only its exact frozen workflow node.</summary>
public sealed class SurrealActionSigningAuthorizationStore(ISurrealExecutor executor)
{
    private const string Conflict = "ACTION_PARTICIPANT_AUTHORIZATION_CHANGED";

    public Task<ActionSigningAuthorization?> GetAsync(string id, CancellationToken ct)
        => executor.QuerySingleAsync<ActionSigningAuthorization>(SurrealQuery<ActionSigningAuthorization>.Key(id), ct);

    public Task<SagaSteps?> GetStepAsync(Guid runId, Guid nodeId, CancellationToken ct)
        => executor.QuerySingleAsync<SagaSteps>(SurrealQuery<SagaSteps>.Key(ActionWorkflowIdentity.StepId(runId, nodeId)), ct);

    public async Task<AZOAResult<ActionSigningAuthorization>> AuthorizeAsync(ActionSigningAuthorization audit,
        WorkflowSigningWitness witness, Execution expected, SagaSteps step, CancellationToken ct)
    {
        var prior = await GetAsync(audit.Id, ct);
        if (prior is not null) return Replay(prior, audit);
        var credential = SigningAuthorityFence.ForRunAdmission(witness.Credential, "participant_credential");
        var grants = SigningAuthorityFence.ForNodeAdmission(witness, "participant_grants");
        if (credential is null || grants is null || witness.AuthorizationId?.ToString("N") != audit.Id
            || CanonicalJson.Hash(witness) != CanonicalJson.HashCanonical(audit.WitnessJson)
            || audit.ActorId != witness.Credential.ActorUserId.ToString("N") || audit.TenantId != witness.Credential.TenantId?.ToString("N")
            || audit.RunId != expected.RunId.ToString("N") || audit.NodeId != expected.NodeId.ToString("N")
            || audit.ExpectedVersion != expected.Version || step.Id != ActionWorkflowIdentity.StepId(expected.RunId, expected.NodeId)
            || witness.Action.RunId != expected.RunId || witness.Action.NodeId != expected.NodeId
            || witness.Action.IntentHash != audit.IntentHash || audit.ExecutionId != expected.Id.ToString("N"))
            return AZOAResult<ActionSigningAuthorization>.Failure("Participant authorization does not match the frozen operation.");
        // raw: current participant credential/grants, original run/node, immutable consent receipt and exact saga wake commit together.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.Add(SurrealQuery.Of("""
            LET $_participant_run = (UPDATE ONLY type::record('action_run', $_participant_run_id) SET version += 1
                WHERE status IN ['Running', 'AwaitingReconciliation'] AND user_id = type::record('user', $_participant_actor)
                    AND (($_participant_has_tenant AND acting_tenant_id = type::record('user', $_participant_tenant))
                        OR (!$_participant_has_tenant AND acting_tenant_id = NONE)) RETURN AFTER)
            """).WithParam("_participant_run_id", audit.RunId).WithParam("_participant_actor", audit.ActorId)
            .WithParam("_participant_has_tenant", audit.TenantId is not null).WithParam("_participant_tenant", audit.TenantId ?? ""));
        statements.Add(SurrealQuery.Of("IF $_participant_run = NONE { THROW 'ACTION_PARTICIPANT_AUTHORIZATION_CHANGED' }"));
        statements.AddRange(credential);
        statements.AddRange(grants);
        statements.Add(SurrealQuery.Of("LET $_participant_node = (SELECT * FROM ONLY type::record('action_node_execution', $_participant_execution))")
            .WithParam("_participant_execution", audit.ExecutionId));
        statements.Add(SurrealQuery.Of("""
            IF $_participant_node = NONE OR $_participant_node.version != $_participant_version
                OR $_participant_node.run_id != type::record('action_run', $_participant_run_id)
                OR $_participant_node.node_id != type::record('action_node', $_participant_node_id)
                OR $_participant_node.state != $_participant_state OR $_participant_node.state NOT IN ['Pending', 'Running']
                OR $_participant_node.recovery_state NOT IN ['NeedsParticipantAuthorization', 'NeedsOperator']
                OR $_participant_node.signing_authority_json != NONE OR $_participant_node.admission_rejection != NONE
                OR $_participant_node.frozen_input != $_participant_input
                OR crypto::sha256($_participant_node.frozen_input) != $_participant_intent
                { THROW 'ACTION_PARTICIPANT_AUTHORIZATION_CHANGED' }
            """).WithParam("_participant_version", audit.ExpectedVersion).WithParam("_participant_run_id", audit.RunId)
            .WithParam("_participant_node_id", audit.NodeId).WithParam("_participant_state", expected.State.ToString())
            .WithParam("_participant_input", expected.FrozenInput).WithParam("_participant_intent", audit.IntentHash));
        statements.Add(SurrealQuery.Of("LET $_participant_step = (SELECT * FROM ONLY type::record('saga_steps', $_participant_step_id))")
            .WithParam("_participant_step_id", step.Id));
        statements.Add(SurrealQuery.Of("""
            IF $_participant_step = NONE OR $_participant_step.status != 'Parked' OR $_participant_step.is_compensation
                OR $_participant_step.saga_name != $_participant_saga OR $_participant_step.correlation_key != $_participant_correlation
                OR $_participant_step.step_name != $_participant_step_name OR $_participant_step.step_idempotency_key != $_participant_key
                OR $_participant_step.gate_id != $_participant_gate OR $_participant_step.payload != $_participant_payload
                OR $_participant_step.admission_payload_hash != $_participant_payload_hash
                { THROW 'ACTION_PARTICIPANT_AUTHORIZATION_CHANGED' }
            """).WithParam("_participant_saga", ActionWorkflowSaga.Name).WithParam("_participant_correlation", SagaOpaqueKey.Encode(expected.RunId.ToString()))
            .WithParam("_participant_step_name", SagaOpaqueKey.Encode(expected.NodeId.ToString()))
            .WithParam("_participant_key", SagaOpaqueKey.Encode(SagaKeys.StepIdempotencyKey(expected.RunId.ToString(), expected.NodeId.ToString())))
            .WithParam("_participant_gate", SagaOpaqueKey.Encode($"{(expected.RecoveryState == "NeedsOperator" ? "operator" : "participant")}:{expected.NodeId:N}"))
            .WithParam("_participant_payload", step.Payload).WithParam("_participant_payload_hash", step.AdmissionPayloadHash));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.UpdateOnly<ActionNodeExecution>(audit.ExecutionId)
            .Where(row => row.Version == audit.ExpectedVersion).Set(row => row.Version, audit.ExpectedVersion + 1)
            .Set(row => row.SigningAuthorityJson, audit.WitnessJson).Set(row => row.RecoveryState, "ParticipantAuthorized")
            .Unset(row => row.Error).Build(), "participant_node"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.UpdateOnly<ActionRun>(audit.RunId)
            .Set(row => row.Status, ActionRun.ActionRunStatusKind.Running).Build(), "participant_run"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.UpdateOnly<SagaSteps>(step.Id)
            .Set(row => row.Status, SagaSteps.StepStatus.Pending).Set(row => row.NextRunAt, audit.CreatedAt)
            .Set(row => row.UpdatedAt, audit.CreatedAt).Unset(row => row.GateId).Unset(row => row.ClaimedAt).Build(), "participant_step"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(audit), "participant_audit"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var transaction = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var result = await executor.ExecuteAsync(transaction, ct);
                result.EnsureAllOk();
                return result;
            }, ct);
            return AZOAResult<ActionSigningAuthorization>.Success(audit);
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal)
            || SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var saved = await GetAsync(audit.Id, ct);
            return saved is null ? AZOAResult<ActionSigningAuthorization>.Failure("The participant authority or frozen operation changed.") : Replay(saved, audit);
        }
    }

    private static AZOAResult<ActionSigningAuthorization> Replay(ActionSigningAuthorization saved, ActionSigningAuthorization expected)
        => saved.ActorId == expected.ActorId && saved.TenantId == expected.TenantId && saved.RequestHash == expected.RequestHash
            ? AZOAResult<ActionSigningAuthorization>.Success(saved)
            : AZOAResult<ActionSigningAuthorization>.Failure("Participant request key belongs to another exact operation.");
}
