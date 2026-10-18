using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>Outcome of a guarded node-admin transaction.</summary>
public enum NodeAdminCommit
{
    Committed,
    /// <summary>A CAS predicate matched no row (a concurrent writer won); re-read and retry.</summary>
    Conflict,
    /// <summary>A record id or UNIQUE index collided.</summary>
    Duplicate,
}

/// <summary>Typed SurrealDB access for node-admin identity, roles, challenges, enrollments and sessions; see <c>Services/NodeAdmin/AGENTS.md</c> §storage.</summary>
public sealed class SurrealNodeAdminStore(ISurrealExecutor executor)
{
    private const string CasMarker = "NODE_ADMIN_CAS";
    private const int MaxRows = 1000;

    public Task<NodeAdminAccount?> GetAdminAsync(string recordId, CancellationToken ct)
        => executor.QuerySingleAsync<NodeAdminAccount>(SurrealQuery<NodeAdminAccount>.Key(recordId), ct);

    public Task<NodeAdminAccount?> GetAdminByUsernameAsync(string username, CancellationToken ct)
        => executor.QuerySingleAsync<NodeAdminAccount>(SurrealQuery<NodeAdminAccount>.From().Where(row => row.Username == username).Limit(1), ct);

    public async Task<IReadOnlyList<NodeAdminAccount>> ListAdminsAsync(CancellationToken ct)
        => await executor.QueryAsync<NodeAdminAccount>(SurrealQuery<NodeAdminAccount>.From().OrderBy(row => row.Username).Limit(MaxRows), ct);

    public Task<NodeAdminCredential?> GetCredentialAsync(string fingerprint, CancellationToken ct)
        => executor.QuerySingleAsync<NodeAdminCredential>(SurrealQuery<NodeAdminCredential>.Key(fingerprint), ct);

    public async Task<IReadOnlyList<NodeAdminCredential>> ListCredentialsAsync(string adminRecordId, CancellationToken ct)
        => await executor.QueryAsync<NodeAdminCredential>(SurrealQuery<NodeAdminCredential>.From()
            .Where(row => row.AdminId == adminRecordId).OrderBy(row => row.CreatedAt).Limit(MaxRows), ct);

    public async Task<IReadOnlyList<NodeAdminCredential>> ListAllCredentialsAsync(CancellationToken ct)
        => await executor.QueryAsync<NodeAdminCredential>(SurrealQuery<NodeAdminCredential>.From()
            .OrderBy(row => row.CreatedAt).Limit(MaxRows * 4), ct);

    /// <summary>Unconsumed enrollments of one admin (invalidated by an MFA reset).</summary>
    public async Task<IReadOnlyList<NodeAdminEnrollment>> ListOpenEnrollmentsAsync(string adminRecordId, CancellationToken ct)
        => await executor.QueryAsync<NodeAdminEnrollment>(SurrealQuery<NodeAdminEnrollment>.From()
            .Where(row => row.AdminId == adminRecordId && row.ConsumedAt == null).Limit(50), ct);

    public Task<NodeAdminRole?> GetRoleAsync(string roleId, CancellationToken ct)
        => executor.QuerySingleAsync<NodeAdminRole>(SurrealQuery<NodeAdminRole>.Key(roleId), ct);

    public async Task<IReadOnlyList<NodeAdminRole>> ListRolesAsync(CancellationToken ct)
        => await executor.QueryAsync<NodeAdminRole>(SurrealQuery<NodeAdminRole>.From().OrderBy(row => row.Name).Limit(MaxRows), ct);

    public Task<NodeAdminDirectory?> GetDirectoryAsync(CancellationToken ct)
        => executor.QuerySingleAsync<NodeAdminDirectory>(SurrealQuery<NodeAdminDirectory>.Key(NodeAdminDirectory.SingletonId), ct);

    public Task<NodeAdminRootState?> GetRootStateAsync(CancellationToken ct)
        => executor.QuerySingleAsync<NodeAdminRootState>(SurrealQuery<NodeAdminRootState>.Key(NodeAdminRootState.SingletonId), ct);

    public Task<NodeAdminEnrollment?> GetEnrollmentAsync(string recordId, CancellationToken ct)
        => executor.QuerySingleAsync<NodeAdminEnrollment>(SurrealQuery<NodeAdminEnrollment>.Key(recordId), ct);

    public Task<NodeAdminEnrollment?> GetEnrollmentByTokenHashAsync(string tokenHash, CancellationToken ct)
        => executor.QuerySingleAsync<NodeAdminEnrollment>(SurrealQuery<NodeAdminEnrollment>.From()
            .Where(row => row.TokenHash == tokenHash).Limit(1), ct);

    public Task<NodeAdminSession?> GetSessionAsync(string recordId, CancellationToken ct)
        => executor.QuerySingleAsync<NodeAdminSession>(SurrealQuery<NodeAdminSession>.Key(recordId), ct);

    /// <summary>Live (unexpired) sessions of one principal, newest first.</summary>
    public async Task<IReadOnlyList<NodeAdminSession>> ListSessionsAsync(string principal, DateTimeOffset now, CancellationToken ct)
        => await executor.QueryAsync<NodeAdminSession>(SurrealQuery<NodeAdminSession>.From()
            .Where(row => row.Principal == principal && row.ExpiresAt > now)
            .OrderByDescending(row => row.AuthTime).Limit(200), ct);

    /// <summary>Records single use of a stateless challenge; <see cref="NodeAdminCommit.Duplicate"/> means it was already used (replay).</summary>
    public Task<NodeAdminCommit> RecordChallengeUseAsync(NodeAdminChallenge marker, CancellationToken ct)
        => CommitAsync(null, [SurrealWriter.Create(marker)], ct);

    public async Task CreateSessionAsync(NodeAdminSession session, CancellationToken ct)
        => (await executor.ExecuteAsync(SurrealWriter.Create(session), ct)).EnsureAllOk();

    public Task<bool> TryRevokeSessionAsync(string recordId, DateTimeOffset now, CancellationToken ct)
        => SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var response = await executor.ExecuteAsync(SurrealWriter.UpdateOnly<NodeAdminSession>(recordId)
                .Where(row => row.RevokedAt == null)
                .Set(row => row.RevokedAt, now).Build(), ct);
            response.EnsureAllOk();
            return response.GetValues<NodeAdminSession>(0).Count() == 1;
        }, ct);

    /// <summary>Revokes every live session of a principal (best effort; the revision bump is the authoritative cut).</summary>
    public async Task RevokeSessionsOfAsync(string principal, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var session in await ListSessionsAsync(principal, now, ct))
            if (session.RevokedAt is null)
                await TryRevokeSessionAsync(session.Id, now, ct);
    }

    /// <summary>Deletes expired challenges, enrollments and sessions in batches until a short batch, at most <paramref name="maxBatches"/> per table.</summary>
    public async Task<int> SweepExpiredAsync(DateTimeOffset cutoff, CancellationToken ct, int batchSize = 500, int maxBatches = 50)
    {
        var removed = 0;
        removed += await SweepAsync(async () => (await executor.QueryAsync<NodeAdminChallenge>(SurrealQuery<NodeAdminChallenge>.From()
                .Where(row => row.ExpiresAt < cutoff).Limit(batchSize), ct)).Select(row => row.Id).ToArray(),
            id => SurrealWriter.DeleteOnly<NodeAdminChallenge>(id).Where(item => item.ExpiresAt < cutoff).Build(), batchSize, maxBatches, ct);
        removed += await SweepAsync(async () => (await executor.QueryAsync<NodeAdminEnrollment>(SurrealQuery<NodeAdminEnrollment>.From()
                .Where(row => row.ExpiresAt < cutoff).Limit(batchSize), ct)).Select(row => row.Id).ToArray(),
            id => SurrealWriter.DeleteOnly<NodeAdminEnrollment>(id).Where(item => item.ExpiresAt < cutoff).Build(), batchSize, maxBatches, ct);
        removed += await SweepAsync(async () => (await executor.QueryAsync<NodeAdminSession>(SurrealQuery<NodeAdminSession>.From()
                .Where(row => row.ExpiresAt < cutoff).Limit(batchSize), ct)).Select(row => row.Id).ToArray(),
            id => SurrealWriter.DeleteOnly<NodeAdminSession>(id).Where(item => item.ExpiresAt < cutoff).Build(), batchSize, maxBatches, ct);
        return removed;
    }

    public Task<NodeAdminSession?> GetSessionByHandleAsync(string handle, CancellationToken ct)
        => executor.QuerySingleAsync<NodeAdminSession>(SurrealQuery<NodeAdminSession>.From().Where(row => row.Handle == handle).Limit(1), ct);

    /// <summary>
    /// Commits typed conditional mutations atomically. With <paramref name="expectedDirectoryRevision"/> the
    /// directory epoch is CASed in the same transaction (M1). Every mutation must return a row or the batch rolls back;
    /// <paramref name="deletes"/> run last without that requirement (their guard is the directory CAS).
    /// </summary>
    public async Task<NodeAdminCommit> CommitAsync(long? expectedDirectoryRevision, IReadOnlyList<SurrealQuery> mutations, CancellationToken ct,
        IReadOnlyList<SurrealQuery>? deletes = null)
    {
        // raw: BEGIN/COMMIT only; the directory CAS and the typed mutations must commit or roll back together (M1).
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        if (expectedDirectoryRevision is { } revision)
        {
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<NodeAdminDirectory>(NodeAdminDirectory.SingletonId)
                .Where(row => row.Revision == revision).Set(row => row.Revision, revision + 1).Build(),
                "node_admin_directory", "node_admin_directory"));
            statements.Add(SurrealGeneratedStatementScope.RequireResult("node_admin_directory", CasMarker));
        }
        for (var index = 0; index < mutations.Count; index++)
        {
            var variable = "node_admin_mutation_" + (char)('a' + index);
            statements.Add(SurrealGeneratedStatementScope.Assign(mutations[index], $"node_admin_m{index}", variable));
            statements.Add(SurrealGeneratedStatementScope.RequireResult(variable, CasMarker));
        }
        if (deletes is not null)
            for (var index = 0; index < deletes.Count; index++)
                statements.Add(SurrealGeneratedStatementScope.Apply(deletes[index], $"node_admin_d{index}"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var batch = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(batch, ct);
                response.EnsureAllOk();
                return true;
            }, ct);
            return NodeAdminCommit.Committed;
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(CasMarker, StringComparison.Ordinal))
        {
            return NodeAdminCommit.Conflict;
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            return NodeAdminCommit.Duplicate;
        }
    }

    private async Task<int> SweepAsync(Func<Task<string[]>> nextBatch, Func<string, SurrealQuery> delete, int batchSize, int maxBatches,
        CancellationToken ct)
    {
        var removed = 0;
        for (var batch = 0; batch < maxBatches; batch++)
        {
            var ids = await nextBatch();
            foreach (var id in ids)
            {
                (await executor.ExecuteAsync(delete(id), ct)).EnsureAllOk();
                removed++;
            }
            if (ids.Length < batchSize) break;
        }
        return removed;
    }
}
