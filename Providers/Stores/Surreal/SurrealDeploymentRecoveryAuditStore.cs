using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealDeploymentRecoveryAuditStore(ISurrealExecutor executor) : IDeploymentRecoveryAuditStore
{
    private const string Conflict = "DEPLOYMENT_RECOVERY_AUDIT_CONFLICT";
    /// <inheritdoc/>
    public Task<DeploymentRecoveryAudit?> GetAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<DeploymentRecoveryAudit>(SurrealQuery<DeploymentRecoveryAudit>.Key(id), ct);

    /// <inheritdoc/>
    public async Task<DeploymentRecoveryAudit> ClaimAsync(DeploymentOperation parent, DeploymentOperation head, CancellationToken ct = default)
    {
        var candidate = NativeRecoveryAncestry.Begin(parent, head);
        try
        {
            var result = await executor.ExecuteAsync(SurrealWriter.Create(candidate), ct);
            result.EnsureAllOk();
            return result.GetValues<DeploymentRecoveryAudit>(0).Single();
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var existing = await GetAsync(candidate.Id, ct);
            return existing is not null && existing.ParentOperationId == candidate.ParentOperationId
                && existing.HeadOperationId == candidate.HeadOperationId && existing.ActorUserId == candidate.ActorUserId
                && existing.HeadIntentHash == candidate.HeadIntentHash && existing.HeadPredecessorId == candidate.HeadPredecessorId
                && existing.TenantId == candidate.TenantId ? existing
                : throw new InvalidOperationException("Recovery ancestry identity conflicts with its original owner.");
        }
    }

    /// <inheritdoc/>
    public async Task<DeploymentRecoveryAudit?> AdvanceAsync(string id, long expectedVersion, CancellationToken ct = default)
    {
        var cursor = await GetAsync(id, ct);
        if (cursor is null || cursor.Version != expectedVersion) return null;
        if (cursor.State != "Running") return cursor;
        var entries = new List<DeploymentRecoveryAuditEntry>();
        var witnesses = new List<DeploymentOperation>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < NativeRecoveryAncestry.PageSize && cursor.State == "Running"; index++)
        {
            var next = cursor.NextOperationId;
            if (next is null || !visited.Add(next)
                || await executor.QuerySingleAsync<DeploymentRecoveryAuditEntry>(SurrealQuery<DeploymentRecoveryAuditEntry>.Key(NativeRecoveryAncestry.EntryId(id, next)), ct) is not null)
            { cursor.State = "Invalid"; cursor.Detail = "Recovery ancestry contains a repeated or missing link."; break; }
            var row = await executor.QuerySingleAsync<DeploymentOperation>(SurrealQuery<DeploymentOperation>.Key(next), ct);
            var entry = row is null ? null : NativeRecoveryAncestry.Inspect(cursor, row);
            if (entry is null)
            { cursor.State = "Invalid"; cursor.Detail = "Recovery ancestry has inconsistent ownership, seal or transaction evidence."; break; }
            entries.Add(entry); witnesses.Add(row!); NativeRecoveryAncestry.Advance(cursor, entry);
        }
        var update = SurrealWriter.UpdateOnly<DeploymentRecoveryAudit>(id)
            .Where(row => row.Version == expectedVersion && row.State == "Running")
            .Set(row => row.ExpectedSuccessorId, cursor.ExpectedSuccessorId).Set(row => row.Digest, cursor.Digest)
            .Set(row => row.InspectedCount, cursor.InspectedCount).Set(row => row.State, cursor.State)
            .Set(row => row.Version, expectedVersion + 1).Set(row => row.UpdatedAt, DateTimeOffset.UtcNow);
        update = cursor.NextOperationId is null ? update.Unset(row => row.NextOperationId) : update.Set(row => row.NextOperationId, cursor.NextOperationId);
        update = cursor.Detail is null ? update.Unset(row => row.Detail) : update.Set(row => row.Detail, cursor.Detail);
        // raw: immutable visited evidence and the durable page cursor advance together under exact sealed-record guards.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        for (var index = 0; index < witnesses.Count; index++)
        {
            var witness = witnesses[index];
            var guard = SurrealWriter.UpdateOnly<DeploymentOperation>(witness.Id)
                .Where(row => row.Version == witness.Version && row.ParentOperationId == cursor.ParentOperationId
                    && row.ActorUserId == cursor.ActorUserId && row.TenantId == cursor.TenantId
                    && row.ReplacedByOperationId == witness.ReplacedByOperationId && row.ReplacesOperationId == witness.ReplacesOperationId
                    && row.RequestJson == witness.RequestJson && row.HistoryJson == witness.HistoryJson)
                .Set(row => row.Version, witness.Version).Build();
            var scoped = SurrealGeneratedStatementScope.Assign(guard, "audit_witness" + index, "_audit_witness");
            statements.Add(scoped);
            statements.Add(SurrealQuery.Of("IF $_audit_witness = NONE OR $_audit_witness = [] { THROW 'DEPLOYMENT_RECOVERY_AUDIT_CONFLICT' }"));
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(entries[index]), "audit_entry" + index));
        }
        var mutation = SurrealGeneratedStatementScope.Assign(update.Build(), "audit_cursor", "_audit_saved");
        statements.Add(mutation);
        statements.Add(SurrealQuery.Of("IF $_audit_saved = NONE OR $_audit_saved = [] { THROW 'DEPLOYMENT_RECOVERY_AUDIT_CONFLICT' }"));
        var resultIndex = statements.Count;
        statements.Add(SurrealQuery.Of("RETURN $_audit_saved")); statements.Add(SurrealQuery.Of("COMMIT"));
        try
        {
            var result = await executor.ExecuteAsync(SurrealQuery.Combine(statements.ToArray()), ct);
            result.EnsureAllOk();
            return result.GetValues<DeploymentRecoveryAudit>(resultIndex).SingleOrDefault();
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)) { return null; }
    }
}
