using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealHostedProviderAttemptStore(ISurrealExecutor executor) : IHostedProviderAttemptStore
{
    /// <inheritdoc/>
    public async Task<HostedProviderCallback?> AdmitCallbackAsync(HostedProviderCallback candidate, CancellationToken ct)
    {
        try
        {
            var created = await KycEvidenceFence.ExecuteAsync(executor, SurrealWriter.Create(candidate), ct);
            created.EnsureAllOk();
            return created.GetValues<HostedProviderCallback>(0).Single();
        }
        catch (SurrealStatementException ex) when (SurrealIdempotencyLedger.IsUniqueViolation(ex.Message))
        {
            var current = await executor.QuerySingleAsync<HostedProviderCallback>(SurrealQuery<HostedProviderCallback>.Key(candidate.Id), ct);
            if (current is null) throw;
            return current.AttemptId == candidate.AttemptId && current.BodyDigest == candidate.BodyDigest ? current : null;
        }
    }

    /// <inheritdoc/>
    public async Task CompleteCallbackAsync(string id, CancellationToken ct)
    {
        var response = await KycEvidenceFence.ExecuteAsync(executor, SurrealWriter.UpdateOnly<HostedProviderCallback>(id)
            .Where(row => !row.Processed).Set(row => row.Processed, true).Build(), ct);
        response.EnsureAllOk();
    }

    /// <inheritdoc/>
    public async Task<HostedProviderAttempt> EnsureAsync(HostedProviderAttempt candidate, CancellationToken ct)
    {
        var existing = await GetAsync(candidate.Id, ct);
        if (existing is not null) return existing;
        try
        {
            var result = await KycEvidenceFence.ExecuteAsync(executor, SurrealWriter.Create(candidate), ct);
            result.EnsureAllOk();
            return result.GetValues<HostedProviderAttempt>(0).Single();
        }
        catch (SurrealStatementException ex) when (SurrealIdempotencyLedger.IsUniqueViolation(ex.Message))
        {
            var raced = await GetAsync(candidate.Id, ct);
            if (raced is not null) return raced;
            throw;
        }
    }

    /// <inheritdoc/>
    public Task<HostedProviderAttempt?> GetAsync(string id, CancellationToken ct)
        => executor.QuerySingleAsync<HostedProviderAttempt>(SurrealQuery<HostedProviderAttempt>.Key(id), ct);

    /// <inheritdoc/>
    public Task<HostedProviderAttempt?> GetByRemoteAsync(string adapter, string origin, string sessionId, CancellationToken ct)
        => executor.QuerySingleAsync<HostedProviderAttempt>(SurrealQuery<HostedProviderAttempt>.From()
            .Where(row => row.AdapterKey == adapter && row.Origin == origin && row.RemoteSessionId == sessionId).Limit(1), ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HostedProviderAttempt>> ListDueAsync(DateTimeOffset now, CancellationToken ct)
        => await executor.QueryAsync<HostedProviderAttempt>(SurrealQuery<HostedProviderAttempt>.From()
            .Where(row => !row.Quarantined && !row.RecoveryCompleted && row.NextAttemptAt <= now)
            .OrderBy(row => row.NextAttemptAt).ThenBy(row => row.Id).Limit(50), ct);

    /// <inheritdoc/>
    public async Task ScheduleAsync(string id, DateTimeOffset nextAttemptAt, bool completed, CancellationToken ct)
    {
        var response = await KycEvidenceFence.ExecuteAsync(executor, SurrealWriter.UpdateOnly<HostedProviderAttempt>(id)
            .Where(row => !row.RecoveryCompleted).Set(row => row.NextAttemptAt, nextAttemptAt)
            .Set(row => row.RecoveryCompleted, completed).Build(), ct);
        response.EnsureAllOk();
    }

    /// <inheritdoc/>
    public async Task<HostedProviderAttempt?> ClaimAsync(HostedSession session, bool allowReplay, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await GetAsync(session.Id, ct);
        if (existing is null || session.CreationStartedAt is null || session.State != HostedSessionStates.Claimed || session.FlowKind != HostedSessionFlowKinds.Kyc
            || existing.ProviderKey != session.ProviderKey || existing.SubjectId != session.SubjectUserId || existing.TenantId != session.TenantId) return null;
        var scheduled = await KycEvidenceFence.ExecuteAsync(executor, SurrealWriter.UpdateOnly<HostedProviderAttempt>(existing.Id)
            .Where(row => row.Version == existing.Version).Set(row => row.NextAttemptAt, now.AddMinutes(2)).Build(), ct);
        scheduled.EnsureAllOk();
        var update = SurrealWriter.UpdateOnly<HostedProviderAttempt>(existing.Id)
            .Where(row => row.Version == existing.Version && !row.Quarantined)
            .Where(row => row.LeaseExpiresAt == null || row.LeaseExpiresAt <= now)
            .Set(row => row.LeaseToken, Guid.NewGuid().ToString("N")).Set(row => row.LeaseExpiresAt, now.AddMinutes(1))
            .Set(row => row.SendStartedAt, existing.SendStartedAt ?? now)
            .Set(row => row.Version, existing.Version + 1);
        if (!allowReplay) update = update.Where(row => row.SendStartedAt == null);
        // raw: a replay may send only while its exact KYC attempt and tenant trust authority are current.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HostedSession>(session.Id)
            .Where(row => row.State == HostedSessionStates.Claimed && row.StateVersion == session.StateVersion
                && row.CreationToken == session.CreationToken && row.CreationStartedAt != null && row.ProviderSessionId == null)
            .Set(row => row.ModifiedDate, (DateTimeOffset?)now).Build(), "provider_creation_owner", "_creation_owner"));
        statements.Add(SurrealQuery.Of("IF $_creation_owner = NONE OR $_creation_owner = [] { THROW 'HOSTED_CREATION_AUTHORITY_CHANGED' }"));
        statements.AddRange(HostedKycCreationFence.Create(session));
        statements.Add(update.Build());
        statements.Add(SurrealQuery.Of("COMMIT"));
        try
        {
            var result = await KycEvidenceFence.ExecuteAsync(executor, SurrealQuery.Combine(statements.ToArray()), ct);
            result.EnsureAllOk();
            return result.GetValues<HostedProviderAttempt>(statements.Count - 2).SingleOrDefault();
        }
        catch (SurrealStatementException ex) when (ex.Message.Contains(HostedKycCreationFence.FailureMarker, StringComparison.Ordinal)
            || ex.Message.Contains("HOSTED_CREATION_AUTHORITY_CHANGED", StringComparison.Ordinal)) { return null; }
    }

    /// <inheritdoc/>
    public async Task<HostedProviderAttempt?> SaveCreationAsync(HostedProviderAttempt lease, HostedSessionCreation creation, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var update = SurrealWriter.UpdateOnly<HostedProviderAttempt>(lease.Id)
            .Where(row => row.Version == lease.Version && row.LeaseToken == lease.LeaseToken && row.LeaseExpiresAt > now)
            .Where(row => row.RemoteSessionId == null || row.RemoteSessionId == creation.ProviderSessionId)
            .Set(row => row.RemoteSessionId, creation.ProviderSessionId).Set(row => row.RedirectUrl, creation.RedirectUrl)
            .Unset(row => row.LeaseToken).Unset(row => row.LeaseExpiresAt).Set(row => row.Version, lease.Version + 1);
        update = creation.ExpiresAt is null ? update.Unset(row => row.ExpiresAt) : update.Set(row => row.ExpiresAt, creation.ExpiresAt);
        var result = await KycEvidenceFence.ExecuteAsync(executor, update.Build(), ct);
        result.EnsureAllOk();
        return result.GetValues<HostedProviderAttempt>(0).SingleOrDefault();
    }

    /// <inheritdoc/>
    public async Task<bool> PinTerminalAsync(string hostedSessionId, string remoteSessionId, string decisionId, string digest, CancellationToken ct)
    {
        var pin = await KycEvidenceFence.ExecuteAsync(executor, SurrealWriter.UpdateOnly<HostedSession>(hostedSessionId)
            .Where(row => row.ProviderSessionId == remoteSessionId && row.ProviderDecisionId == null)
            .Set(row => row.ProviderDecisionId, decisionId).Set(row => row.ProviderEvidenceDigest, digest)
            .Set(row => row.ProviderEvidenceConflict, false).Build(), ct);
        pin.EnsureAllOk();
        var current = await executor.QuerySingleAsync<HostedSession>(SurrealQuery<HostedSession>.Key(hostedSessionId), ct);
        if (current is null || current.ProviderSessionId != remoteSessionId) return false;
        if (current.ProviderDecisionId == decisionId && current.ProviderEvidenceDigest == digest)
            return current.ProviderEvidenceConflict != true;
        var conflict = await KycEvidenceFence.ExecuteAsync(executor, SurrealWriter.UpdateOnly<HostedSession>(hostedSessionId)
            .Where(row => row.ProviderSessionId == remoteSessionId && row.ProviderDecisionId != null)
            .Set(row => row.ProviderEvidenceConflict, true).Build(), ct);
        conflict.EnsureAllOk();
        return false;
    }
}
