using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using Row = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionNodeExecution;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealActionNodeExecutionStore(ISurrealExecutor executor) : IActionNodeExecutionStore
{
    /// <inheritdoc/>
    public async Task<AZOAResult<ActionNodeExecution?>> TryAdmitAsync(ActionNodeExecution execution, CancellationToken ct = default, long? expectedRunVersion = null)
    {
        if (execution.State != ActionNodeState.Pending || string.IsNullOrWhiteSpace(execution.FrozenInput) || expectedRunVersion is null)
            return AZOAResult<ActionNodeExecution?>.Failure("Frozen input and a Pending execution are required.");
        var candidate = execution.Clone();
        candidate.State = ActionNodeState.Running;
        candidate.StartedAt = DateTime.UtcNow;
        var claimed = await UpdateCoreAsync(candidate, ActionNodeState.Pending, ct, expectedRunVersion);
        return AZOAResult<ActionNodeExecution?>.Success(claimed.IsError ? null : claimed.Result);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<ActionNodeExecution>> CreateAsync(ActionNodeExecution execution, CancellationToken ct = default)
    {
        if (execution is null || execution.Id == Guid.Empty)
            return AZOAResult<ActionNodeExecution>.Failure("An execution identity is required.");
        try
        {
            var response = await executor.ExecuteAsync(SurrealWriter.Create(ToRow(execution)), ct);
            response.EnsureAllOk();
            return AZOAResult<ActionNodeExecution>.Success(ToDomain(response.GetValues<Row>(0).Single()));
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            return AZOAResult<ActionNodeExecution>.Failure("An execution already exists for this run and node.");
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<ActionNodeExecution>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var row = await executor.QuerySingleAsync<Row>(SurrealQuery<Row>.Key(id.ToString("N")), ct);
        return row is null ? AZOAResult<ActionNodeExecution>.Failure("Node execution not found.")
            : AZOAResult<ActionNodeExecution>.Success(ToDomain(row));
    }

    /// <inheritdoc/>
    public Task<AZOAResult<ActionNodeExecution>> UpdateAsync(ActionNodeExecution execution,
        ActionNodeState? expectedState = null, CancellationToken ct = default)
        => UpdateCoreAsync(execution, expectedState, ct, null);

    private async Task<AZOAResult<ActionNodeExecution>> UpdateCoreAsync(ActionNodeExecution execution,
        ActionNodeState? expectedState, CancellationToken ct, long? expectedRunVersion)
    {
        if (execution is null || execution.Id == Guid.Empty)
            return AZOAResult<ActionNodeExecution>.Failure("An execution identity is required.");
        var row = ToRow(execution);
        var current = await GetByIdAsync(execution.Id, ct);
        if (current.IsError || current.Result is null) return current;
        if (current.Result.Version != execution.Version || !ActionOperationIdentity.CanUpdate(current.Result, execution))
            return AZOAResult<ActionNodeExecution>.Failure("Node execution version or immutable intent changed.");
        if (current.Result.State == ActionNodeState.Pending && execution.State == ActionNodeState.Running && expectedRunVersion is null)
            return AZOAResult<ActionNodeExecution>.Failure("New effects require a current scheduler admission decision.");
        if (expectedRunVersion is null && ((current.Result.SigningAuthorityJson is null && execution.SigningAuthorityJson is not null)
            || (current.Result.AdmissionRejection is null && execution.AdmissionRejection is not null)))
            return AZOAResult<ActionNodeExecution>.Failure("Signing authority and no-effect rejection require atomic admission.");
        WorkflowSigningWitness? signing = null;
        IReadOnlyList<SurrealQuery>? signingFence = null;
        ActionFrozenNodeInput? admittedInput = null;
        if (expectedRunVersion is not null)
        {
            admittedInput = JsonSerializer.Deserialize<ActionFrozenNodeInput>(execution.FrozenInput!);
            if (admittedInput is null || admittedInput.Version != 1 || admittedInput.RunId != execution.RunId || admittedInput.NodeId != execution.NodeId)
                return AZOAResult<ActionNodeExecution>.Failure("Frozen action identity is invalid.");
        }
        if (expectedRunVersion is not null && execution.AdmissionRejection is null)
        {
            var input = admittedInput!;
            var scopes = ActionSigningScopes.Required(input.NodeType, input.Config.GetRawText());
            if (scopes is null) return AZOAResult<ActionNodeExecution>.Failure("Signing scope requirements are unavailable.");
            if (scopes.Count > 0)
            {
                signing = execution.SigningAuthorityJson is null ? null : JsonSerializer.Deserialize<WorkflowSigningWitness>(execution.SigningAuthorityJson);
                var expectedAction = new ActionEffectAuthority(input.RunId, input.NodeId, input.ActorUserId, input.ActingTenantId,
                    CanonicalJson.HashCanonical(execution.FrozenInput!));
                var expectedScopes = scopes.Append(AZOA.WebAPI.Core.AzoaScopes.ActionExecute).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
                if (signing is null || signing.Action != expectedAction
                    || !signing.RequiredScopes.Order(StringComparer.Ordinal).SequenceEqual(expectedScopes)
                    || (signingFence = SigningAuthorityFence.ForNodeAdmission(signing, "node_signing")) is null)
                    return AZOAResult<ActionNodeExecution>.Failure("Current participant signing authorization is required.");
            }
        }
        var update = SurrealWriter.UpdateOnly<Row>(row.Id).Where(item => item.Version == row.Version)
            .Set(item => item.State, row.State).Set(item => item.Version, row.Version + 1)
            .Set(item => item.StartedAt, row.StartedAt)
            .Set(item => item.ManualAdvanceReleased, row.ManualAdvanceReleased);
        if (expectedState is { } state)
        {
            var expected = (Row.ActionNodeState)(int)state;
            update = update.Where(item => item.State == expected);
        }
        update = row.WaitUntil is null ? update.Unset(item => item.WaitUntil) : update.Set(item => item.WaitUntil, row.WaitUntil);
        update = row.Output is null ? update.Unset(item => item.Output) : update.Set(item => item.Output, row.Output);
        update = row.Error is null ? update.Unset(item => item.Error) : update.Set(item => item.Error, row.Error);
        update = row.TxHash is null ? update.Unset(item => item.TxHash) : update.Set(item => item.TxHash, row.TxHash);
        update = row.ChainType is null ? update.Unset(item => item.ChainType) : update.Set(item => item.ChainType, row.ChainType);
        update = row.EndedAt is null ? update.Unset(item => item.EndedAt) : update.Set(item => item.EndedAt, row.EndedAt);
        update = row.FrozenInput is null ? update.Unset(item => item.FrozenInput) : update.Set(item => item.FrozenInput, row.FrozenInput);
        update = row.SigningAuthorityJson is null ? update.Unset(item => item.SigningAuthorityJson) : update.Set(item => item.SigningAuthorityJson, row.SigningAuthorityJson);
        update = row.AdmissionRejection is null ? update.Unset(item => item.AdmissionRejection) : update.Set(item => item.AdmissionRejection, row.AdmissionRejection);
        update = row.ExternalOperationJson is null ? update.Unset(item => item.ExternalOperationJson) : update.Set(item => item.ExternalOperationJson, row.ExternalOperationJson);
        update = row.RecoveryState is null ? update.Unset(item => item.RecoveryState) : update.Set(item => item.RecoveryState, row.RecoveryState);
        // raw: node outcomes and fresh admission invalidate the scheduler epoch under the run lock; see AGENTS.md.
        var runGuard = expectedRunVersion is null
            ? SurrealQuery.Of("LET $_node_run = (UPDATE ONLY type::record('action_run', $_node_run_id) SET version += 1 RETURN AFTER)")
                .WithParam("_node_run_id", execution.RunId.ToString("N"))
            : SurrealQuery.Of("LET $_node_run = (UPDATE ONLY type::record('action_run', $_node_run_id) SET version += 1 WHERE version = $_scheduler_version AND status = 'Running' RETURN AFTER)")
                .WithParam("_node_run_id", execution.RunId.ToString("N")).WithParam("_scheduler_version", expectedRunVersion.Value);
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN"), runGuard,
            SurrealQuery.Of("IF $_node_run = NONE { THROW 'ACTION_SCHEDULER_DECISION_CHANGED' }") };
        if (admittedInput is not null)
            statements.Add(SurrealQuery.Of("""
                IF $_node_run.user_id != type::record('user', $_admission_actor)
                    OR (($_admission_has_tenant AND $_node_run.acting_tenant_id != type::record('user', $_admission_tenant))
                        OR (!$_admission_has_tenant AND $_node_run.acting_tenant_id != NONE))
                    { THROW 'ACTION_SCHEDULER_DECISION_CHANGED' }
                """).WithParam("_admission_actor", admittedInput.ActorUserId.ToString("N"))
                .WithParam("_admission_has_tenant", admittedInput.ActingTenantId.HasValue)
                .WithParam("_admission_tenant", admittedInput.ActingTenantId?.ToString("N") ?? ""));
        if (signing is not null && signingFence is not null)
        {
            statements.Add(SurrealQuery.Of("""
                IF $_node_run.user_id != type::record('user', $_signing_actor)
                    OR (($_signing_tenant_present AND $_node_run.acting_tenant_id != type::record('user', $_signing_tenant))
                        OR (!$_signing_tenant_present AND $_node_run.acting_tenant_id != NONE))
                    { THROW 'SIGNING_AUTHORITY_CHANGED' }
                """).WithParam("_signing_actor", signing.Credential.ActorUserId.ToString("N"))
                .WithParam("_signing_tenant_present", signing.Credential.TenantId.HasValue)
                .WithParam("_signing_tenant", signing.Credential.TenantId?.ToString("N") ?? ""));
            if (signing.AuthorizationId is { } authorizationId)
            {
                statements.Add(SurrealQuery.Of("LET $_node_authorization = (SELECT * FROM ONLY type::record('action_signing_authorization', $_node_authorization_id))")
                    .WithParam("_node_authorization_id", authorizationId.ToString("N")));
                statements.Add(SurrealQuery.Of("""
                    IF $_node_authorization = NONE OR $_node_authorization.execution_id != $_signing_execution_id
                        OR crypto::sha256($_node_authorization.witness_json) != $_signing_witness_hash
                        { THROW 'SIGNING_AUTHORITY_CHANGED' }
                    """).WithParam("_signing_execution_id", execution.Id.ToString("N"))
                    .WithParam("_signing_witness_hash", CanonicalJson.Hash(signing)));
            }
            else
                statements.Add(SurrealQuery.Of("""
                    IF $_node_run.authentication_witness_json = NONE
                        OR crypto::sha256($_node_run.authentication_witness_json) != $_signing_credential_hash
                        { THROW 'SIGNING_AUTHORITY_CHANGED' }
                    """).WithParam("_signing_credential_hash", CanonicalJson.Hash(signing.Credential)));
            statements.AddRange(signingFence);
        }
        var updateIndex = statements.Count;
        statements.Add(SurrealGeneratedStatementScope.Apply(update.Build(), "node_outcome"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var transaction = SurrealQuery.Combine(statements.ToArray());
        Row? saved;
        try
        {
            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var result = await executor.ExecuteAsync(transaction, ct);
                result.EnsureAllOk();
                return result;
            }, ct);
            saved = response.GetValues<Row>(updateIndex).SingleOrDefault();
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains("ACTION_SCHEDULER_DECISION_CHANGED", StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
        {
            return AZOAResult<ActionNodeExecution>.Failure("Workflow scheduler decision changed.");
        }
        if (saved is null) return AZOAResult<ActionNodeExecution>.Failure("Node execution version/state changed.");
        execution.Version = saved.Version;
        return AZOAResult<ActionNodeExecution>.Success(ToDomain(saved));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<ActionNodeExecution>>> GetByRunIdAsync(Guid runId, CancellationToken ct = default)
    {
        var id = SurrealLink.ToLink("action_run", runId.ToString("N"));
        var rows = await executor.QueryAsync<Row>(SurrealQuery<Row>.From().Where(row => row.RunId == id)
            .OrderBy(row => row.StartedAt), ct);
        return AZOAResult<IEnumerable<ActionNodeExecution>>.Success(rows.Select(ToDomain).ToArray());
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<ActionNodeExecution>> GetByRunAndNodeAsync(Guid runId, Guid nodeId, CancellationToken ct = default)
    {
        var run = SurrealLink.ToLink("action_run", runId.ToString("N")); var node = SurrealLink.ToLink("action_node", nodeId.ToString("N"));
        var row = await executor.QuerySingleAsync<Row>(SurrealQuery<Row>.From()
            .Where(item => item.RunId == run && item.NodeId == node).Limit(1), ct);
        return row is null ? AZOAResult<ActionNodeExecution>.Failure("Node execution not found.")
            : AZOAResult<ActionNodeExecution>.Success(ToDomain(row));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<ActionNodeExecution?>> TryClaimPendingAsync(Guid runId, Guid nodeId, CancellationToken ct = default)
    {
        var existing = await GetByRunAndNodeAsync(runId, nodeId, ct);
        if (existing.IsError || existing.Result is null)
            return AZOAResult<ActionNodeExecution?>.Failure(existing.Message);
        if (existing.Result.State != ActionNodeState.Pending)
            return AZOAResult<ActionNodeExecution?>.Success(null);
        var row = existing.Result;
        row.State = ActionNodeState.Running; row.StartedAt = DateTime.UtcNow;
        var claimed = await UpdateAsync(row, ActionNodeState.Pending, ct);
        return AZOAResult<ActionNodeExecution?>.Success(claimed.IsError ? null : claimed.Result);
    }

    private static Row ToRow(ActionNodeExecution row) => new()
    {
        Id = row.Id.ToString("N"), RunId = SurrealLink.ToLink("action_run", row.RunId.ToString("N"))!, NodeId = SurrealLink.ToLink("action_node", row.NodeId.ToString("N"))!,
        State = (Row.ActionNodeState)(int)row.State, Version = row.Version,
        Output = row.Output, Error = row.Error, TxHash = row.TxHash, ChainType = row.ChainType,
        StartedAt = new DateTimeOffset(DateTime.SpecifyKind(row.StartedAt, DateTimeKind.Utc)),
        EndedAt = row.EndedAt is { } ended ? new DateTimeOffset(DateTime.SpecifyKind(ended, DateTimeKind.Utc)) : null,
        WaitUntil = row.WaitUntil is { } wait ? new DateTimeOffset(DateTime.SpecifyKind(wait, DateTimeKind.Utc)) : null,
        FrozenInput = row.FrozenInput, SigningAuthorityJson = row.SigningAuthorityJson, AdmissionRejection = row.AdmissionRejection, RecoveryState = row.RecoveryState, ManualAdvanceReleased = row.ManualAdvanceReleased,
        ExternalOperationJson = row.ExternalOperation is null ? null : JsonSerializer.Serialize(row.ExternalOperation),
    };

    internal static ActionNodeExecution ToDomain(Row row) => new()
    {
        Id = SurrealRecordGuid.Parse(row.Id), RunId = SurrealRecordGuid.Parse(row.RunId), NodeId = SurrealRecordGuid.Parse(row.NodeId),
        State = (ActionNodeState)(int)row.State, Version = row.Version,
        Output = row.Output, Error = row.Error, TxHash = row.TxHash, ChainType = row.ChainType,
        StartedAt = row.StartedAt.UtcDateTime, EndedAt = row.EndedAt?.UtcDateTime,
        WaitUntil = row.WaitUntil?.UtcDateTime,
        FrozenInput = row.FrozenInput, SigningAuthorityJson = row.SigningAuthorityJson, AdmissionRejection = row.AdmissionRejection, RecoveryState = row.RecoveryState, ManualAdvanceReleased = row.ManualAdvanceReleased,
        ExternalOperation = row.ExternalOperationJson is null ? null
            : JsonSerializer.Deserialize<ActionExternalOperationReference>(row.ExternalOperationJson)
                ?? throw new InvalidOperationException("Node operation envelope is unreadable."),
    };
}
