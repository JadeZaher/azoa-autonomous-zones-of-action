// SPDX-License-Identifier: UNLICENSED

using System.Text.Json;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using SurrealForge.Client.Idempotency;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealHostedSessionStore : IHostedSessionStore, ICustodialHostedSessionStore
{
    private readonly ISurrealExecutor _executor;

    public SurrealHostedSessionStore(ISurrealExecutor executor, ILogger<SurrealHostedSessionStore>? logger = null)
        => _executor = executor;

    /// <inheritdoc/>
    public async Task<AZOAResult<HostedSession>> ClaimAdmissionAsync(
        HostedSession candidate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var existing = await GetByAdmissionAsync(candidate.FlowKind, candidate.ProviderKey, candidate.AdmissionKey, ct);
        if (existing.Result is not null) return existing;
        var released = await GetByIdAsync(Guid.ParseExact(candidate.Id, "N"), ct);
        if (released.Result is { State: HostedSessionStates.Abandoned, CreationStartedAt: null } prior
            && !string.IsNullOrWhiteSpace(prior.CreationToken)
            && prior.AdmissionKey == candidate.AdmissionKey + ":resolved:" + candidate.Id
            && prior.FlowKind == candidate.FlowKind && prior.ProviderKey == candidate.ProviderKey)
        {
            var reclaim = SurrealWriter.UpdateOnly<HostedSession>(candidate.Id)
                .Where(row => row.State == HostedSessionStates.Abandoned && row.StateVersion == prior.StateVersion
                    && row.CreationStartedAt == null && row.ProviderSessionId == null)
                .Set(row => row.State, HostedSessionStates.Claimed).Set(row => row.StateVersion, prior.StateVersion + 1)
                .Set(row => row.AdmissionKey, candidate.AdmissionKey).Set(row => row.CreationToken, candidate.CreationToken!)
                .Set(row => row.CreationClaimedAt, (DateTimeOffset?)(candidate.CreationClaimedAt ?? DateTimeOffset.UtcNow))
                .Set(row => row.ModifiedDate, (DateTimeOffset?)(candidate.ModifiedDate ?? DateTimeOffset.UtcNow))
                .Unset(row => row.CompletedAt).Build();
            var reclaimed = await _executor.ExecuteAsync(reclaim, ct);
            reclaimed.EnsureAllOk();
            var claimed = reclaimed.GetValues<HostedSession>(0).SingleOrDefault();
            return claimed is null ? await GetByIdAsync(Guid.ParseExact(candidate.Id, "N"), ct)
                : AZOAResult<HostedSession>.Success(FromStorage(claimed));
        }
        try
        {
            var response = await _executor.ExecuteAsync(SurrealWriter.Create(ToStorage(candidate)), ct);
            response.EnsureAllOk();
            return AZOAResult<HostedSession>.Success(FromStorage(response.GetValues<HostedSession>(0).Single()));
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var raced = await GetByAdmissionAsync(candidate.FlowKind, candidate.ProviderKey, candidate.AdmissionKey, ct);
            if (raced.Result is not null) return raced;
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ReleaseUnsentAdmissionAsync(Guid id, string creationToken, CancellationToken ct = default)
    {
        var observed = await GetByIdAsync(id, ct);
        if (observed.Result is null) return false;
        var prior = observed.Result;
        var query = SurrealWriter.UpdateOnly<HostedSession>(id.ToString("N"))
            .Where(row => row.State == HostedSessionStates.Claimed && row.CreationToken == creationToken
                && row.ProviderSessionId == null && row.StateVersion == prior.StateVersion)
            .Set(row => row.State, HostedSessionStates.Abandoned).Set(row => row.StateVersion, prior.StateVersion + 1)
            .Set(row => row.AdmissionKey, prior.AdmissionKey + ":resolved:" + prior.Id)
            .Set(row => row.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow)
            .Unset(row => row.CreationStartedAt).Unset(row => row.CreationQuarantinedAt).Build();
        var response = await _executor.ExecuteAsync(query, ct);
        response.EnsureAllOk();
        return response.GetValues<HostedSession>(0).Any();
    }

    /// <inheritdoc/>
    public Task<AZOAResult<HostedSession>> TryBeginCreationAsync(Guid id, string creationToken, DateTimeOffset now, CancellationToken ct = default)
        => TryBeginCreationCoreAsync(id, creationToken, now, null, ct);

    /// <inheritdoc/>
    public Task<AZOAResult<HostedSession>> TryBeginCustodialCreationAsync(Guid id, string creationToken, DateTimeOffset now,
        TenantCustodialWitness witness, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(witness);
        return TryBeginCreationCoreAsync(id, creationToken, now, witness, ct);
    }

    private async Task<AZOAResult<HostedSession>> TryBeginCreationCoreAsync(Guid id, string creationToken, DateTimeOffset now,
        TenantCustodialWitness? custody, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(creationToken);
        var observed = await GetByIdAsync(id, ct);
        if (observed.Result is null) return AZOAResult<HostedSession>.Success(null!);
        var expectedVersion = observed.Result.StateVersion;
        var query = SurrealWriter.UpdateOnly<HostedSession>(id.ToString("N"))
            .Where(row => row.State == HostedSessionStates.Claimed && row.StateVersion == expectedVersion
                && row.CreationToken == creationToken && row.CreationStartedAt == null
                && row.CreationQuarantinedAt == null && row.ProviderSessionId == null)
            .Set(row => row.CreationStartedAt, (DateTimeOffset?)now)
            .Set(row => row.StateVersion, expectedVersion + 1).Set(row => row.ModifiedDate, (DateTimeOffset?)now).Build();
        var fenced = observed.Result.FlowKind == HostedSessionFlowKinds.Kyc
            && observed.Result.AdmissionKey.StartsWith("kyc-attempt:", StringComparison.Ordinal);
        IReadOnlyList<SurrealQuery> custodyGuards = [];
        if (custody is not null)
        {
            if (!fenced || observed.Result.SubjectUserId is not { } subjectUserId
                || SurrealRecordGuid.Parse(subjectUserId) != custody.UserId
                || SurrealRecordGuid.ParseOptional(observed.Result.TenantId) != custody.Authority.Credential.ActorUserId)
                return AZOAResult<HostedSession>.Success(null!);
            var guards = TenantCustodialAuthorityFence.Create(custody, AzoaScopes.KycSubmit);
            if (guards is null) return AZOAResult<HostedSession>.Success(null!);
            custodyGuards = guards;
        }
        if (fenced)
        {
            // raw: send ownership and current KYC attempt authority commit together; see AGENTS.md.
            var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
            statements.AddRange(custodyGuards);
            statements.AddRange(HostedKycCreationFence.Create(observed.Result));
            statements.Add(query);
            statements.Add(SurrealQuery.Of("COMMIT"));
            query = SurrealQuery.Combine(statements.ToArray());
        }
        try
        {
            var response = await KycEvidenceFence.ExecuteAsync(_executor, query, ct);
            response.EnsureAllOk();
            var row = response.GetValues<HostedSession>(fenced ? response.Statements.Count - 2 : 0).SingleOrDefault();
            return AZOAResult<HostedSession>.Success(row is null ? null! : FromStorage(row));
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(HostedKycCreationFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(TenantCustodialAuthorityFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
        {
            return AZOAResult<HostedSession>.Success(null!);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HostedSession>> ListReconciliationAsync(
        DateTimeOffset before, int limit, CancellationToken ct = default)
    {
        var query = SurrealQuery<HostedSession>.From()
            .Where(row => row.State == HostedSessionStates.Claimed && row.ProviderSessionId == null && (row.CreationClaimedAt <= before || (row.CreationClaimedAt == null && row.CreatedDate <= before)))
            .OrderBy(row => row.CreatedDate).Limit(Math.Clamp(limit, 1, 100));
        var rows = await _executor.QueryAsync<HostedSession>(query, ct);
        return rows.Select(FromStorage).ToArray();
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<HostedSession>> ReconcileAdmissionAsync(
        HostedSession expected, Guid actorId, string reason, HostedSessionCreation? providerSession,
        DateTimeOffset now, CancellationToken ct = default)
    {
        var cutoff = now.AddMinutes(-5);
        var provablyUnsent = expected.CreationStartedAt is null && !string.IsNullOrWhiteSpace(expected.CreationToken);
        var audit = string.IsNullOrWhiteSpace(expected.ReconciliationAuditJson)
            ? new List<ReconciliationAudit>()
            : JsonSerializer.Deserialize<List<ReconciliationAudit>>(expected.ReconciliationAuditJson)!;
        audit.Add(new(actorId.ToString("N"), now, reason,
            providerSession is not null ? "AttachVerifiedSession" : provablyUnsent ? "ReleaseUnsent" : "QuarantineSent"));
        var mutation = SurrealWriter.UpdateOnly<HostedSession>(expected.Id)
            .Where(row => row.State == HostedSessionStates.Claimed && row.StateVersion == expected.StateVersion)
            .Where(row => row.ProviderSessionId == null && (row.CreationClaimedAt <= cutoff || (row.CreationClaimedAt == null && row.CreatedDate <= cutoff)))
            .Set(row => row.StateVersion, expected.StateVersion + 1)
            .Set(row => row.ReconciledByUserId, actorId.ToString("N"))
            .Set(row => row.ReconciledAt, (DateTimeOffset?)now)
            .Set(row => row.ReconciliationReason, reason)
            .Set(row => row.ReconciliationAuditJson, JsonSerializer.Serialize(audit))
            .Set(row => row.ModifiedDate, (DateTimeOffset?)now);
        if (providerSession is null)
        {
            if (provablyUnsent)
                mutation = mutation.Where(row => row.CreationStartedAt == null)
                    .Set(row => row.State, HostedSessionStates.Abandoned)
                    .Set(row => row.AdmissionKey, expected.AdmissionKey + ":resolved:" + expected.Id)
                    .Set(row => row.CompletedAt, (DateTimeOffset?)now);
            else
                mutation = mutation.Set(row => row.CreationQuarantinedAt, (DateTimeOffset?)now);
        }
        else
        {
            mutation = mutation.Set(row => row.State, HostedSessionStates.AwaitingSubject)
                .Set(row => row.ProviderSessionId, providerSession.ProviderSessionId)
                .Set(row => row.RedirectUrl, providerSession.RedirectUrl);
            mutation = providerSession.ProviderCorrelationId is null ? mutation.Unset(row => row.ProviderCorrelationId) : mutation.Set(row => row.ProviderCorrelationId, providerSession.ProviderCorrelationId);
            mutation = providerSession.ExpiresAt is null ? mutation.Unset(row => row.ExpiresAt) : mutation.Set(row => row.ExpiresAt, providerSession.ExpiresAt);
        }
        if (providerSession is not null)
            return await AttachWithAliasesAsync(expected, providerSession.ProviderSessionId,
                providerSession.ProviderCorrelationId, mutation.Build(), ct);
        var response = await _executor.ExecuteAsync(mutation.Build(), ct);
        response.EnsureAllOk();
        var row = response.GetValues<HostedSession>(0).SingleOrDefault();
        return AZOAResult<HostedSession>.Success(row is null ? null! : FromStorage(row));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<HostedSession>> GetByProviderSessionAsync(
        string flowKind, string providerKey, string providerSessionId, CancellationToken ct = default)
    {
        var query = SurrealQuery<HostedSession>.From()
            .Where(row => row.FlowKind == flowKind && row.ProviderKey == providerKey
                && (row.ProviderSessionId == providerSessionId || row.ProviderCorrelationId == providerSessionId))
            .Limit(1);
        var row = await _executor.QuerySingleAsync<HostedSession>(query, ct);
        return AZOAResult<HostedSession>.Success(row is null ? null! : FromStorage(row));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<HostedSession>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _executor.QuerySingleAsync<HostedSession>(SurrealQuery<HostedSession>.Key(id.ToString("N")), ct);
        return AZOAResult<HostedSession>.Success(row is null ? null! : FromStorage(row));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<HostedSession>> TryAttachProviderSessionAsync(
        Guid id, long expectedStateVersion, string providerSessionId, string? providerCorrelationId,
        string redirectUrl, DateTimeOffset? expiresAt, string creationToken, CancellationToken ct = default)
    {
        var observed = await GetByIdAsync(id, ct);
        if (observed.Result is null) return AZOAResult<HostedSession>.Success(null!);
        // CAS on the caller's version; only an operator quarantine may intervene, which a late original response resolves.
        var currentVersion = observed.Result.StateVersion;
        if (currentVersion != expectedStateVersion && observed.Result.CreationQuarantinedAt is null)
            return AZOAResult<HostedSession>.Success(null!);
        var query = SurrealWriter.UpdateOnly<HostedSession>(id.ToString("N"))
            .Where(row => row.StateVersion == currentVersion)
            .Where(row => row.State == HostedSessionStates.Claimed && row.CreationToken == creationToken
                && row.CreationStartedAt != null)
            .Where(row => row.ProviderSessionId == null)
            .Set(row => row.ProviderSessionId, providerSessionId)
            .Set(row => row.RedirectUrl, redirectUrl)
            .Set(row => row.CreationCompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow)
            .Set(row => row.State, HostedSessionStates.AwaitingSubject)
            .Set(row => row.StateVersion, currentVersion + 1)
            .Set(row => row.ModifiedDate, (DateTimeOffset?)DateTimeOffset.UtcNow);
        query = providerCorrelationId is null ? query.Unset(row => row.ProviderCorrelationId) : query.Set(row => row.ProviderCorrelationId, providerCorrelationId);
        query = expiresAt is null ? query.Unset(row => row.ExpiresAt) : query.Set(row => row.ExpiresAt, expiresAt);
        return await AttachWithAliasesAsync(observed.Result, providerSessionId, providerCorrelationId, query.Build(), ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<HostedSession>> TryClaimResumeAsync(
        Guid id, long expectedStateVersion, string token, DateTimeOffset now,
        DateTimeOffset leaseExpiresAt, CancellationToken ct = default)
    {
        var query = SurrealWriter.UpdateOnly<HostedSession>(id.ToString("N"))
            .Where(row => row.StateVersion == expectedStateVersion)
            .Where(row => row.State == HostedSessionStates.AwaitingSubject
                || row.State == HostedSessionStates.AwaitingOutcome
                || (row.State == HostedSessionStates.Resuming
                    && (row.ResumeLeaseExpiresAt == null || row.ResumeLeaseExpiresAt <= now)))
            .Set(row => row.State, HostedSessionStates.Resuming)
            .Set(row => row.StateVersion, expectedStateVersion + 1)
            .Set(row => row.ResumeLeaseToken, token)
            .Set(row => row.ResumeLeaseExpiresAt, (DateTimeOffset?)leaseExpiresAt)
            .Set(row => row.ModifiedDate, (DateTimeOffset?)now)
            .Build();
        var response = await _executor.ExecuteAsync(query, ct);
        response.EnsureAllOk();
        var row = response.GetValues<HostedSession>(0).SingleOrDefault();
        return AZOAResult<HostedSession>.Success(row is null ? null! : FromStorage(row));
    }

    /// <inheritdoc/>
    public async Task<bool> RenewResumeLeaseAsync(
        Guid id, string token, DateTimeOffset now, DateTimeOffset leaseExpiresAt,
        CancellationToken ct = default)
    {
        var query = SurrealWriter.UpdateOnly<HostedSession>(id.ToString("N"))
            .Where(row => row.State == HostedSessionStates.Resuming
                && row.ResumeLeaseToken == token && row.ResumeLeaseExpiresAt > now)
            .Set(row => row.ResumeLeaseExpiresAt, (DateTimeOffset?)leaseExpiresAt)
            .Build();
        var response = await _executor.ExecuteAsync(query, ct);
        response.EnsureAllOk();
        return response.GetValues<HostedSession>(0).Any();
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<HostedSession>> TryFinishResumeAsync(
        Guid id, long expectedStateVersion, string token, DateTimeOffset now,
        string newState, string? authoritativeOutcome, string? outcomeDetail,
        string? resumeTargetId, CancellationToken ct = default)
    {
        if (newState is not (HostedSessionStates.Completed or HostedSessionStates.Failed
            or HostedSessionStates.AwaitingOutcome))
            return AZOAResult<HostedSession>.Failure("Invalid hosted resumption transition.");
        var query = SurrealWriter.UpdateOnly<HostedSession>(id.ToString("N"))
            .Where(row => row.State == HostedSessionStates.Resuming && row.StateVersion == expectedStateVersion)
            .Where(row => row.ResumeLeaseToken == token && row.ResumeLeaseExpiresAt > now)
            .Set(row => row.State, newState)
            .Set(row => row.StateVersion, expectedStateVersion + 1)
            .Set(row => row.ModifiedDate, (DateTimeOffset?)now)
            .Unset(row => row.ResumeLeaseToken)
            .Unset(row => row.ResumeLeaseExpiresAt);
        query = authoritativeOutcome is null ? query.Unset(row => row.AuthoritativeOutcome) : query.Set(row => row.AuthoritativeOutcome, authoritativeOutcome);
        query = outcomeDetail is null ? query.Unset(row => row.OutcomeDetail) : query.Set(row => row.OutcomeDetail, outcomeDetail);
        query = resumeTargetId is null ? query.Unset(row => row.ResumeTargetId) : query.Set(row => row.ResumeTargetId, resumeTargetId);
        query = HostedSessionStates.IsTerminal(newState) ? query.Set(row => row.CompletedAt, (DateTimeOffset?)now) : query.Unset(row => row.CompletedAt);
        var response = await _executor.ExecuteAsync(query.Build(), ct);
        response.EnsureAllOk();
        var row = response.GetValues<HostedSession>(0).SingleOrDefault();
        return AZOAResult<HostedSession>.Success(row is null ? null! : FromStorage(row));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<HostedSession>> TryTransitionAsync(
        Guid id, long expectedStateVersion, string newState, string? authoritativeOutcome,
        string? outcomeDetail, string? resumeTargetId, CancellationToken ct = default)
    {
        if (newState == HostedSessionStates.Resuming)
            return AZOAResult<HostedSession>.Failure("Resumption requires an owned lease.");
        var now = DateTimeOffset.UtcNow;
        var query = SurrealWriter.UpdateOnly<HostedSession>(id.ToString("N"))
            .Where(row => row.StateVersion == expectedStateVersion)
            .Where(row => row.State == HostedSessionStates.Claimed
                || row.State == HostedSessionStates.AwaitingSubject
                || row.State == HostedSessionStates.AwaitingOutcome)
            .Set(row => row.State, newState)
            .Set(row => row.StateVersion, expectedStateVersion + 1)
            .Set(row => row.ModifiedDate, (DateTimeOffset?)now);
        query = authoritativeOutcome is null ? query.Unset(row => row.AuthoritativeOutcome) : query.Set(row => row.AuthoritativeOutcome, authoritativeOutcome);
        query = outcomeDetail is null ? query.Unset(row => row.OutcomeDetail) : query.Set(row => row.OutcomeDetail, outcomeDetail);
        query = resumeTargetId is null ? query.Unset(row => row.ResumeTargetId) : query.Set(row => row.ResumeTargetId, resumeTargetId);
        query = HostedSessionStates.IsTerminal(newState) ? query.Set(row => row.CompletedAt, (DateTimeOffset?)now) : query.Unset(row => row.CompletedAt);
        var response = await _executor.ExecuteAsync(query.Build(), ct);
        response.EnsureAllOk();
        var row = response.GetValues<HostedSession>(0).SingleOrDefault();
        return AZOAResult<HostedSession>.Success(row is null ? null! : FromStorage(row));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> RecordCallbackAsync(Guid id, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var observed = await GetByIdAsync(id, ct);
            if (observed.Result is null) return AZOAResult<bool>.Success(false);
            var count = observed.Result.CallbackCount;
            var query = SurrealWriter.UpdateOnly<HostedSession>(id.ToString("N"))
                .Where(row => row.CallbackCount == count)
                .Set(row => row.CallbackCount, checked(count + 1))
                .Set(row => row.LastCallbackAt, (DateTimeOffset?)DateTimeOffset.UtcNow)
                .Build();
            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            if (response.GetValues<HostedSession>(0).Any()) return AZOAResult<bool>.Success(true);
        }
        return AZOAResult<bool>.Success(false);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<HostedSession>> GetByAdmissionAsync(
        string flowKind, string providerKey, string admissionKey, CancellationToken ct)
    {
        var query = SurrealQuery<HostedSession>.From()
            .Where(row => row.FlowKind == flowKind && row.ProviderKey == providerKey && row.AdmissionKey == admissionKey)
            .Limit(1);
        var row = await _executor.QuerySingleAsync<HostedSession>(query, ct);
        return AZOAResult<HostedSession>.Success(row is null ? null! : FromStorage(row));
    }

    private async Task<AZOAResult<HostedSession>> AttachWithAliasesAsync(
        HostedSession expected, string providerSessionId, string? correlationId, SurrealQuery mutation, CancellationToken ct)
    {
        // raw: typed session CAS and provider-alias reservations must commit together; see AGENTS.md.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN"),
            SurrealGeneratedStatementScope.Assign(mutation, "attach", "_attached"),
            SurrealGeneratedStatementScope.RequireResult("_attached", "HOSTED_ATTACHMENT_CHANGED") };
        statements.AddRange(HostedSessionAliasFence.Reserve(expected, providerSessionId, correlationId));
        // Absolute slot: a top-level RETURN drops the statements after it from the result array.
        var resultIndex = statements.Count;
        statements.Add(SurrealQuery.Of("RETURN $_attached"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            var row = response.GetValues<HostedSession>(resultIndex).SingleOrDefault();
            return AZOAResult<HostedSession>.Success(row is null ? null! : FromStorage(row));
        }
        catch (SurrealStatementException exception) when (
            exception.Message.Contains("HOSTED_ATTACHMENT_CHANGED", StringComparison.Ordinal)
            || exception.Message.Contains(HostedSessionAliasFence.ConflictMarker, StringComparison.Ordinal))
        {
            return AZOAResult<HostedSession>.Success(null!);
        }
    }

    private sealed record ReconciliationAudit(string ActorId, DateTimeOffset At, string Reason, string Action);

    private static HostedSession ToStorage(HostedSession session) => new()
    {
        Id = session.Id,
        FlowKind = session.FlowKind,
        ProviderKey = session.ProviderKey,
        AdmissionKey = session.AdmissionKey,
        CreationToken = session.CreationToken,
        CreationClaimedAt = session.CreationClaimedAt,
        CreationStartedAt = session.CreationStartedAt,
        CreationQuarantinedAt = session.CreationQuarantinedAt,
        CreationCompletedAt = session.CreationCompletedAt,
        SubjectUserId = string.IsNullOrWhiteSpace(session.SubjectUserId)
            ? null
            : SurrealLink.ToLink("user", session.SubjectUserId),
        TenantId = string.IsNullOrWhiteSpace(session.TenantId)
            ? null
            : SurrealLink.ToLink("user", session.TenantId),
        ProviderSessionId = session.ProviderSessionId,
        ProviderCorrelationId = session.ProviderCorrelationId,
        RedirectUrl = session.RedirectUrl,
        State = session.State,
        StateVersion = session.StateVersion,
        ResumeLeaseToken = session.ResumeLeaseToken,
        ResumeLeaseExpiresAt = session.ResumeLeaseExpiresAt,
        ReconciledByUserId = session.ReconciledByUserId,
        ReconciledAt = session.ReconciledAt,
        ReconciliationReason = session.ReconciliationReason,
        ReconciliationAuditJson = session.ReconciliationAuditJson,
        AuthoritativeOutcome = session.AuthoritativeOutcome,
        ProviderDecisionId = session.ProviderDecisionId,
        ProviderEvidenceDigest = session.ProviderEvidenceDigest,
        ProviderEvidenceConflict = session.ProviderEvidenceConflict,
        OutcomeDetail = session.OutcomeDetail,
        CallbackCount = session.CallbackCount,
        ResumeTargetId = session.ResumeTargetId,
        CreatedDate = session.CreatedDate.ToUniversalTime(),
        ModifiedDate = session.ModifiedDate?.ToUniversalTime(),
        ExpiresAt = session.ExpiresAt?.ToUniversalTime(),
        LastCallbackAt = session.LastCallbackAt?.ToUniversalTime(),
        CompletedAt = session.CompletedAt?.ToUniversalTime(),
    };

    private static HostedSession FromStorage(HostedSession session) => new()
    {
        Id = session.Id,
        FlowKind = session.FlowKind,
        ProviderKey = session.ProviderKey,
        AdmissionKey = session.AdmissionKey,
        CreationToken = session.CreationToken,
        CreationClaimedAt = session.CreationClaimedAt,
        CreationStartedAt = session.CreationStartedAt,
        CreationQuarantinedAt = session.CreationQuarantinedAt,
        CreationCompletedAt = session.CreationCompletedAt,
        SubjectUserId = string.IsNullOrWhiteSpace(session.SubjectUserId)
            ? null
            : SurrealLink.FromLink(session.SubjectUserId),
        TenantId = string.IsNullOrWhiteSpace(session.TenantId)
            ? null
            : SurrealLink.FromLink(session.TenantId),
        ProviderSessionId = session.ProviderSessionId,
        ProviderCorrelationId = session.ProviderCorrelationId,
        RedirectUrl = session.RedirectUrl,
        State = session.State,
        StateVersion = session.StateVersion,
        ResumeLeaseToken = session.ResumeLeaseToken,
        ResumeLeaseExpiresAt = session.ResumeLeaseExpiresAt,
        ReconciledByUserId = session.ReconciledByUserId,
        ReconciledAt = session.ReconciledAt,
        ReconciliationReason = session.ReconciliationReason,
        ReconciliationAuditJson = session.ReconciliationAuditJson,
        AuthoritativeOutcome = session.AuthoritativeOutcome,
        ProviderDecisionId = session.ProviderDecisionId,
        ProviderEvidenceDigest = session.ProviderEvidenceDigest,
        ProviderEvidenceConflict = session.ProviderEvidenceConflict,
        OutcomeDetail = session.OutcomeDetail,
        CallbackCount = session.CallbackCount,
        ResumeTargetId = session.ResumeTargetId,
        CreatedDate = session.CreatedDate,
        ModifiedDate = session.ModifiedDate,
        ExpiresAt = session.ExpiresAt,
        LastCallbackAt = session.LastCallbackAt,
        CompletedAt = session.CompletedAt,
    };
}
