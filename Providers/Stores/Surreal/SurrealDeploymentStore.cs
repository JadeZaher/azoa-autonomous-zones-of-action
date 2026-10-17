using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed partial class SurrealDeploymentStore(ISurrealExecutor executor) : IDeploymentStore
{
    /// <inheritdoc/>
    public async Task<DeploymentArtifact> AddArtifactAsync(DeploymentArtifact artifact, CancellationToken ct = default)
    {
        try
        {
            var response = await executor.ExecuteAsync(SurrealWriter.Create(artifact), ct);
            response.EnsureAllOk();
            return response.GetValues<DeploymentArtifact>(0).Single();
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            return await GetArtifactAsync(artifact.Id, ct) ?? throw new InvalidOperationException("Artifact claim disappeared.");
        }
    }

    /// <inheritdoc/>
    public Task<DeploymentArtifact?> GetArtifactAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<DeploymentArtifact>(SurrealQuery<DeploymentArtifact>.Key(id), ct);

    /// <inheritdoc/>
    public async Task AddQuoteAsync(NativeQuote quote, CancellationToken ct = default)
    {
        var response = await executor.ExecuteAsync(SurrealWriter.Create(quote), ct);
        response.EnsureAllOk();
    }

    /// <inheritdoc/>
    public Task<NativeQuote?> GetQuoteAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<NativeQuote>(SurrealQuery<NativeQuote>.Key(id), ct);

    /// <inheritdoc/>
    public async Task<NativeQuote> ClaimQuoteAsync(NativeQuote quote, CancellationToken ct = default)
    {
        try { await AddQuoteAsync(quote, ct); return quote; }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        { return await GetQuoteAsync(quote.Id, ct) ?? throw new InvalidOperationException("Native quote claim disappeared."); }
    }

    /// <inheritdoc/>
    public async Task<DeploymentOperation?> ClaimAsync(DeploymentOperation candidate, CancellationToken ct = default)
    {
        try
        {
            var query = SurrealWriter.Create(candidate);
            var response = await executor.ExecuteAsync(Fenced(query, candidate, true).Query, ct);
            response.EnsureAllOk();
            return await GetAsync(candidate.Id, ct) ?? throw new InvalidOperationException("Deployment claim disappeared.");
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            return await GetAsync(candidate.Id, ct) ?? throw new InvalidOperationException("Deployment claim disappeared.");
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(ActionExecutionFence.FailureMarker, StringComparison.Ordinal))
        { return null; }
    }

    /// <inheritdoc/>
    public Task<DeploymentOperation?> GetAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<DeploymentOperation>(SurrealQuery<DeploymentOperation>.Key(id), ct);

    /// <inheritdoc/>
    public async Task<DeploymentOperation?> UpdateAsync(DeploymentOperation row, long expectedVersion, CancellationToken ct = default, bool requireActionAuthority = false)
    {
        var update = SurrealWriter.UpdateOnly<DeploymentOperation>(row.Id)
            .Where(item => item.Version == expectedVersion && item.RecoveryChildId == null && !item.RecoveryProjected && item.ReplacedByOperationId == null
                && item.ParentOperationId == row.ParentOperationId && item.RecoveryBindingHash == row.RecoveryBindingHash)
            .Set(item => item.State, row.State).Set(item => item.Stage, row.Stage)
            .Set(item => item.RecoveryMode, row.RecoveryMode)
            .Set(item => item.HistoryJson, row.HistoryJson).Set(item => item.Generation, row.Generation)
            .Set(item => item.SubmissionAuthorized, row.SubmissionAuthorized)
            .Set(item => item.Version, expectedVersion + 1).Set(item => item.UpdatedAt, DateTimeOffset.UtcNow);
        update = row.PreparedJson is null ? update.Unset(item => item.PreparedJson) : update.Set(item => item.PreparedJson, row.PreparedJson);
        update = row.ResourceId is null ? update.Unset(item => item.ResourceId) : update.Set(item => item.ResourceId, row.ResourceId);
        update = row.EvidenceJson is null ? update.Unset(item => item.EvidenceJson) : update.Set(item => item.EvidenceJson, row.EvidenceJson);
        update = row.Detail is null ? update.Unset(item => item.Detail) : update.Set(item => item.Detail, row.Detail);
        try
        {
            var fenced = Fenced(update.Build(), row, requireActionAuthority);
            var response = await executor.ExecuteAsync(fenced.Query, ct);
            response.EnsureAllOk();
            return response.GetValues<DeploymentOperation>(fenced.MutationIndex).SingleOrDefault();
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(ActionExecutionFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(RecoveryConflict, StringComparison.Ordinal))
        { return null; }
    }

    // raw: action cancellation/fork and domain admission or first authorization share one transaction.
    private static (SurrealQuery Query, int MutationIndex) Fenced(SurrealQuery mutation, DeploymentOperation row, bool required)
    {
        var guards = new List<SurrealQuery>();
        if (required && row.ActionRunId is not null)
        {
            if (row.ActionNodeId is null || row.ActionIntentHash is null) throw new InvalidOperationException("Action deployment authority is incomplete.");
            guards.AddRange(ActionExecutionFence.Create(row.ActionRunId, row.ActionNodeId, row.ActorUserId,
                string.IsNullOrEmpty(row.TenantId) ? null : row.TenantId, row.ActionIntentHash));
        }
        if (row.ParentOperationId is not null)
        {
            guards.Add(SurrealQuery.Of("""
                LET $_linked_parent = (UPDATE ONLY type::record('deployment_operation', $_linked_parent_id)
                    SET version = version WHERE recovery_child_id = $_linked_child_id AND recovery_binding_hash = $_linked_binding
                        AND actor_user_id = $_linked_actor AND tenant_id = $_linked_tenant
                        AND submission_authorized = false AND prepared_json = NONE RETURN AFTER)
                """).WithParam("_linked_parent_id", row.ParentOperationId).WithParam("_linked_child_id", row.Id)
                .WithParam("_linked_binding", row.RecoveryBindingHash).WithParam("_linked_actor", row.ActorUserId).WithParam("_linked_tenant", row.TenantId));
            guards.Add(SurrealQuery.Of("IF $_linked_parent = NONE { THROW 'DEPLOYMENT_RECOVERY_CONFLICT' }"));
        }
        if (guards.Count == 0) return (mutation, 0);
        return (SurrealQuery.Combine(new[] { SurrealQuery.Of("BEGIN") }.Concat(guards)
            .Append(SurrealGeneratedStatementScope.Apply(mutation, "deployment"))
            .Append(SurrealQuery.Of("COMMIT")).ToArray()), guards.Count + 1);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<DeploymentOperation>> ListActorAsync(string actorId, string tenantId, DateTimeOffset snapshot, int offset, int limit, CancellationToken ct = default)
        => executor.QueryAsync<DeploymentOperation>(SurrealQuery<DeploymentOperation>.From()
            .Where(row => row.ActorUserId == actorId && row.TenantId == tenantId && row.CreatedAt <= snapshot)
            .OrderByDescending(row => row.CreatedAt).ThenBy(row => row.Id).Limit(Math.Clamp(limit, 1, 101)).Start(offset), ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<DeploymentOperation>> ListPendingAsync(int limit, CancellationToken ct = default)
        => executor.QueryAsync<DeploymentOperation>(SurrealQuery<DeploymentOperation>.From()
            .Where(row => (row.SubmissionAuthorized && (row.State == "Submitted" || row.State == "NeedsOperator"))
                || (row.State == "Confirmed" && row.ParentOperationId != null && !row.RecoveryProjected))
            .OrderBy(row => row.UpdatedAt).ThenBy(row => row.Id).Limit(Math.Clamp(limit, 1, 100)), ct);
}
