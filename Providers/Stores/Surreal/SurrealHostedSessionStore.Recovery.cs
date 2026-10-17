// SPDX-License-Identifier: UNLICENSED

using System.Text.Json;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.HostedSessions;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Outcome-poll, stalled-session and orphan-supersede persistence; see Services/HostedSessions/AGENTS.md §recovery.</summary>
public sealed partial class SurrealHostedSessionStore : IHostedSessionRecoveryStore
{
    private const int AuditAppendAttempts = 4;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HostedSession>> ListPollDueAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        var query = SurrealQuery<HostedSession>.From()
            .Where(row => (row.FlowKind == HostedSessionFlowKinds.Kyc || row.FlowKind == HostedSessionFlowKinds.Kyb)
                && row.ProviderSessionId != null
                && (row.State == HostedSessionStates.AwaitingSubject || row.State == HostedSessionStates.AwaitingOutcome
                    || (row.State == HostedSessionStates.Resuming && row.ResumeLeaseExpiresAt <= now))
                && (row.NextPollAt == null || row.NextPollAt <= now))
            .OrderBy(row => row.ModifiedDate!).Limit(Math.Clamp(limit, 1, 100));
        var rows = await _executor.QueryAsync<HostedSession>(query, ct);
        return rows.Select(FromStorage).ToArray();
    }

    /// <inheritdoc/>
    public async Task<bool> TryClaimPollAsync(HostedSession observed, DateTimeOffset nextPollAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(observed);
        var observedNext = observed.NextPollAt?.ToUniversalTime();
        var version = observed.StateVersion;
        var query = SurrealWriter.UpdateOnly<HostedSession>(observed.Id)
            .Where(row => row.StateVersion == version && row.NextPollAt == observedNext && row.ProviderSessionId != null)
            .Set(row => row.NextPollAt, (DateTimeOffset?)nextPollAt.ToUniversalTime())
            .Set(row => row.PollAttemptCount, (long?)((observed.PollAttemptCount ?? 0) + 1))
            .Build();
        var response = await _executor.ExecuteAsync(query, ct);
        response.EnsureAllOk();
        return response.GetValues<HostedSession>(0).Any();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HostedSession>> ListStalledAsync(DateTimeOffset before, int limit, CancellationToken ct = default)
    {
        var query = SurrealQuery<HostedSession>.From()
            .Where(row => row.ProviderSessionId != null
                && (row.State == HostedSessionStates.AwaitingSubject || row.State == HostedSessionStates.AwaitingOutcome
                    || row.State == HostedSessionStates.Resuming)
                && row.ModifiedDate <= before)
            .OrderBy(row => row.ModifiedDate!).Limit(Math.Clamp(limit, 1, 100));
        var rows = await _executor.QueryAsync<HostedSession>(query, ct);
        return rows.Select(FromStorage).ToArray();
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<HostedSession>> TrySupersedeOrphanAsync(HostedSession orphan, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orphan);
        var version = orphan.StateVersion;
        var audit = AppendAudit(orphan.ReconciliationAuditJson,
            new ReconciliationAudit("system", now, "Possibly-sent admission superseded by a fresh attempt.", "SupersedeOrphan"));
        var query = SurrealWriter.UpdateOnly<HostedSession>(orphan.Id)
            .Where(row => row.State == HostedSessionStates.Claimed && row.StateVersion == version
                && row.ProviderSessionId == null && row.CreationStartedAt != null)
            .Set(row => row.State, HostedSessionStates.Abandoned)
            .Set(row => row.StateVersion, version + 1)
            .Set(row => row.AdmissionKey, orphan.AdmissionKey + ":superseded:" + orphan.Id)
            .Set(row => row.OutcomeDetail, HostedSessionRecoveryOutcomes.SupersededOrphan)
            .Set(row => row.ReconciliationAuditJson, audit)
            .Set(row => row.CompletedAt, (DateTimeOffset?)now.ToUniversalTime())
            .Set(row => row.ModifiedDate, (DateTimeOffset?)now.ToUniversalTime())
            .Build();
        var response = await _executor.ExecuteAsync(query, ct);
        response.EnsureAllOk();
        var row = response.GetValues<HostedSession>(0).SingleOrDefault();
        return AZOAResult<HostedSession>.Success(row is null ? null! : FromStorage(row));
    }

    /// <inheritdoc/>
    public async Task<int> CountSupersededSinceAsync(string flowKind, Guid subjectUserId, DateTimeOffset since, CancellationToken ct = default)
    {
        var subject = SurrealLink.ToLink("user", subjectUserId.ToString("N"));
        var cutoff = since.ToUniversalTime();
        var query = SurrealQuery<HostedSession>.From()
            .Where(row => row.SubjectUserId == subject && row.FlowKind == flowKind
                && row.OutcomeDetail == HostedSessionRecoveryOutcomes.SupersededOrphan && row.CompletedAt >= cutoff)
            .Limit(100);
        var rows = await _executor.QueryAsync<HostedSession>(query, ct);
        return rows.Count();
    }

    /// <inheritdoc/>
    public async Task<bool> AppendAuditAsync(Guid id, string actor, string action, string reason, DateTimeOffset now, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < AuditAppendAttempts; attempt++)
        {
            var observed = await GetByIdAsync(id, ct);
            if (observed.Result is null) return false;
            var prior = observed.Result.ReconciliationAuditJson;
            var next = AppendAudit(prior, new ReconciliationAudit(actor, now, reason, action));
            // CAS on the audit text only: the append must never bump state_version under a live flow.
            var query = SurrealWriter.UpdateOnly<HostedSession>(id.ToString("N"))
                .Where(row => row.ReconciliationAuditJson == prior)
                .Set(row => row.ReconciliationAuditJson, next)
                .Build();
            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            if (response.GetValues<HostedSession>(0).Any()) return true;
        }
        return false;
    }

    private static string AppendAudit(string? priorJson, ReconciliationAudit entry)
    {
        var audit = string.IsNullOrWhiteSpace(priorJson)
            ? new List<ReconciliationAudit>()
            : JsonSerializer.Deserialize<List<ReconciliationAudit>>(priorJson) ?? [];
        audit.Add(entry);
        return JsonSerializer.Serialize(audit);
    }
}
