using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Actions.Workflow;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using RunRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionRun;
using NodeRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionNodeExecution;
using SourceRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionRunSource;
using SagaRow = AZOA.WebAPI.Persistence.SurrealDb.Models.SagaSteps;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealActionWorkflowAdmissionStore(ISurrealExecutor executor) : IActionWorkflowAdmissionStore
{
    /// <inheritdoc/>
    public async Task EnqueueNodeAsync(Guid runId, Guid nodeId, string payload, CancellationToken ct = default)
    {
        var candidate = Step(runId, nodeId, payload, DateTimeOffset.UtcNow);
        try
        {
            var response = await executor.ExecuteAsync(SurrealWriter.Create(candidate), ct);
            response.EnsureAllOk();
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var existing = await executor.QuerySingleAsync<SagaRow>(SurrealQuery<SagaRow>.Key(candidate.Id), ct)
                ?? throw new InvalidOperationException("Admitted workflow step disappeared.");
            if (existing.CorrelationKey != candidate.CorrelationKey || existing.StepName != candidate.StepName
                || existing.SagaName != candidate.SagaName || existing.AdmissionPayloadHash != candidate.AdmissionPayloadHash)
                throw new InvalidOperationException("Workflow step identity conflicts with its admitted payload.");
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<ActionRun>> AdmitAsync(ActionWorkflowAdmission admission, CancellationToken ct = default)
    {
        var run = admission.Run;
        var existing = await executor.QuerySingleAsync<RunRow>(SurrealQuery<RunRow>.Key(run.Id.ToString("N")), ct);
        if (existing is not null) return Replay(existing, run);
        if (admission.Sources.Count == 0 || admission.Graph.Nodes.All(node => node.Id != admission.EntryNodeId)
            || run.DefinitionSnapshotJson is null || CanonicalJson.HashCanonical(run.DefinitionSnapshotJson) != run.DefinitionSnapshotHash
            || CanonicalJson.Hash(admission.Graph) != run.DefinitionSnapshotHash || admission.Graph.Id != run.ActionId)
            return AZOAResult<ActionRun>.Failure("Frozen workflow admission is incomplete.");
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<SurrealQuery>? authenticationFence = null;
        if (run.AuthenticationWitnessJson is { } authenticationJson)
        {
            var credential = JsonSerializer.Deserialize<SigningCredentialWitness>(authenticationJson);
            if (credential is null || credential.ActorUserId != run.UserId || credential.TenantId != run.ActingTenantId
                || (!credential.OwnerAuthority && !credential.Scopes.Contains(AZOA.WebAPI.Core.AzoaScopes.ActionExecute, StringComparer.Ordinal))
                || (authenticationFence = SigningAuthorityFence.ForRunAdmission(credential, "workflow_auth")) is null)
                return AZOAResult<ActionRun>.Failure("Workflow authentication witness is invalid.");
        }
        // raw: current source authority/quota, frozen run, node claims and first saga step commit together; see AGENTS.md.
        var statements = new List<SurrealQuery>
        {
            SurrealQuery.Of("BEGIN"),
            SurrealQuery.Of("LET $_actor_lock = (UPDATE type::record('user', $_actor) SET is_active = is_active WHERE is_active = true RETURN AFTER)")
                .WithParam("_actor", run.UserId.ToString("N")),
            SurrealQuery.Of("IF array::len($_actor_lock) != 1 { THROW 'ACTION_ADMISSION_ACTOR_CHANGED' }"),
        };
        if (authenticationFence is not null) statements.AddRange(authenticationFence);
        if (admission.AuthorityWitness is { } witnesses)
            statements.AddRange(DeploymentAuthorityFence.Create(witnesses, run.UserId));
        foreach (var source in admission.Sources.DistinctBy(item => item.ActionId))
        {
            var scope = Scope("source", source.ActionId);
            var token = SurrealIdentifierTemplate.Token(scope);
            var hashGuard = source.PublishedHash is null ? SurrealIdentifierTemplate.Fragment($"published_version_hash = NONE")
                : SurrealIdentifierTemplate.Fragment($"published_version_hash = ${token}_hash");
            var sourceGuard = SurrealIdentifierTemplate.Of($"LET ${token} = (UPDATE type::record('action', ${token}_id) SET version = version WHERE version = ${token}_version AND status = 'Active' AND {hashGuard} AND (user_id = type::record('user', $_actor) OR (is_public = true AND (run_access = 'Open' OR invited_user_ids CONTAINS $_actor_link))) RETURN AFTER)")
                .WithParam(scope + "_id", source.ActionId.ToString("N")).WithParam(scope + "_version", source.Version)
                .WithParam("_actor", run.UserId.ToString("N"))
                .WithParam("_actor_link", SurrealLink.ToLink("user", run.UserId.ToString("N"))!);
            if (source.PublishedHash is not null) sourceGuard = sourceGuard.WithParam(scope + "_hash", source.PublishedHash);
            statements.Add(sourceGuard);
            statements.Add(SurrealIdentifierTemplate.Of($"IF array::len(${token}) != 1 {{ THROW 'ACTION_ADMISSION_AUTHORITY_CHANGED' }}"));
            if (source.ConcurrentLimit is { } limit)
            {
                statements.Add(SurrealIdentifierTemplate.Of($"LET ${token}_active = (SELECT VALUE id FROM action_run WHERE user_id = type::record('user', $_actor) AND status NOT IN ['Succeeded', 'Failed', 'Cancelled', 'Forked'] AND (action_id = type::record('action', ${token}_id) OR id IN (SELECT VALUE run_id FROM action_run_source WHERE action_id = type::record('action', ${token}_id))))")
                    .WithParam("_actor", run.UserId.ToString("N")).WithParam(scope + "_id", source.ActionId.ToString("N")));
                statements.Add(SurrealIdentifierTemplate.Of($"IF array::len(${token}_active) >= ${token}_limit {{ THROW 'ACTION_ADMISSION_QUOTA' }}")

                    .WithParam(scope + "_limit", limit));
            }
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(new SourceRow
            {
                Id = $"{run.Id:N}_{source.ActionId:N}", RunId = SurrealLink.ToLink("action_run", run.Id.ToString("N"))!, ActionId = SurrealLink.ToLink("action", source.ActionId.ToString("N"))!, SourceVersion = source.Version,
            }), scope));
        }
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(SurrealActionRunStore.ToRow(run)), "run"));
        foreach (var node in admission.Graph.Nodes)
        {
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(new NodeRow
            {
                Id = Guid.NewGuid().ToString("N"), RunId = SurrealLink.ToLink("action_run", run.Id.ToString("N"))!, NodeId = SurrealLink.ToLink("action_node", node.Id.ToString("N"))!, StartedAt = now,
                State = NodeRow.ActionNodeState.Pending,
            }), Scope("node", node.Id)));
        }
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(Step(run.Id, admission.EntryNodeId,
            JsonSerializer.Serialize(new ActionStepPayload(run.Id, run.ActionId, run.UserId, admission.EntryNodeId)), now)), "saga"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        try
        {
            var response = await executor.ExecuteAsync(SurrealQuery.Combine(statements.ToArray()), ct);
            response.EnsureAllOk();
            return AZOAResult<ActionRun>.Success(run);
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var replay = await executor.QuerySingleAsync<RunRow>(SurrealQuery<RunRow>.Key(run.Id.ToString("N")), ct);
            return replay is null ? AZOAResult<ActionRun>.Failure("Workflow admission conflicted.") : Replay(replay, run);
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains("ACTION_ADMISSION_", StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
        {
            return AZOAResult<ActionRun>.Failure("Workflow source access, revision or concurrency quota changed.");
        }
    }

    private static AZOAResult<ActionRun> Replay(RunRow saved, ActionRun candidate) =>
        SurrealRecordGuid.Parse(saved.UserId) == candidate.UserId
        && SurrealRecordGuid.Parse(saved.ActionId) == candidate.ActionId
        && SurrealRecordGuid.ParseOptional(saved.ActingTenantId) == candidate.ActingTenantId
        && saved.InvocationIdentity == candidate.InvocationIdentity && saved.DefinitionSnapshotHash == candidate.DefinitionSnapshotHash
        && saved.InitialInputJson == candidate.InitialInputJson && saved.InternalDependenciesJson == candidate.InternalDependenciesJson
        && saved.OccurrenceBoundariesJson == candidate.OccurrenceBoundariesJson
            ? AZOAResult<ActionRun>.Success(SurrealActionRunStore.ToDomain(saved))
            : AZOAResult<ActionRun>.Failure("Invocation key was already admitted with different input or authority.");

    private static string Scope(string prefix, Guid id) => prefix + "_" + string.Concat(id.ToByteArray().Select(value => $"{(char)('a' + value / 16)}{(char)('a' + value % 16)}"));

    private static SagaRow Step(Guid runId, Guid nodeId, string payload, DateTimeOffset now) => new()
    {
        Id = ActionWorkflowIdentity.StepId(runId, nodeId),
        CorrelationKey = SagaOpaqueKey.Encode(runId.ToString()), SagaName = ActionWorkflowSaga.Name,
        StepName = SagaOpaqueKey.Encode(nodeId.ToString()),
        StepIdempotencyKey = SagaOpaqueKey.Encode(SagaKeys.StepIdempotencyKey(runId.ToString(), nodeId.ToString())), Payload = payload,
        AdmissionPayloadHash = CanonicalJson.HashCanonical(payload),
        Status = SagaRow.StepStatus.Pending, NextRunAt = now, CreatedAt = now, UpdatedAt = now,
    };
}
