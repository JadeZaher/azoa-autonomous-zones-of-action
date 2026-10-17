// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="IHostedSessionStore"/> that reproduces the SurrealDB
/// store's CONDITIONAL semantics: a transition only applies when the expected
/// state version matches AND the row is still non-terminal. Without that fidelity
/// the redelivery tests would pass against a store that cannot lose a race, which
/// is precisely the failure mode they exist to catch.
/// </summary>
public sealed class InMemoryHostedSessionStore : IHostedSessionStore
{
    private readonly Dictionary<string, HostedSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Every mutation is taken under this lock so a conditional update is ATOMIC,
    /// exactly as the database's conditional UPDATE is. A fake that check-then-writes
    /// without one cannot lose a race, which would make a genuinely concurrent test
    /// as hollow as a sequential one.
    /// </summary>
    private readonly object _gate = new();

    private int _terminalWriteCount;
    private int _callbackRecordCount;

    public int TerminalWriteCount => Volatile.Read(ref _terminalWriteCount);
    public int CallbackRecordCount => Volatile.Read(ref _callbackRecordCount);

    public Task<AZOAResult<HostedSession>> ClaimAdmissionAsync(
        HostedSession candidate,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var existing = _sessions.Values.FirstOrDefault(session =>
                session.FlowKind == candidate.FlowKind
                && session.ProviderKey == candidate.ProviderKey
                && session.AdmissionKey == candidate.AdmissionKey);
            if (existing is not null)
                return Task.FromResult(AZOAResult<HostedSession>.Success(Clone(existing), "Resumed."));

            if (_sessions.TryGetValue(candidate.Id, out var prior))
            {
                if (prior.State != HostedSessionStates.Abandoned || prior.CreationStartedAt is not null
                    || string.IsNullOrWhiteSpace(prior.CreationToken)
                    || prior.AdmissionKey != candidate.AdmissionKey + ":resolved:" + candidate.Id)
                    return Task.FromResult(AZOAResult<HostedSession>.Success(Clone(prior)));
                prior.State = HostedSessionStates.Claimed;
                prior.StateVersion++;
                prior.CreationToken = candidate.CreationToken;
                prior.CreationClaimedAt = candidate.CreationClaimedAt ?? DateTimeOffset.UtcNow;
                prior.AdmissionKey = candidate.AdmissionKey;
                prior.CompletedAt = null;
                return Task.FromResult(AZOAResult<HostedSession>.Success(Clone(prior)));
            }

            _sessions[candidate.Id] = Clone(candidate);
            return Task.FromResult(AZOAResult<HostedSession>.Success(Clone(candidate), "Claimed."));
        }
    }

    public Task<AZOAResult<HostedSession>> GetByProviderSessionAsync(
        string flowKind,
        string providerKey,
        string providerSessionId,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var found = _sessions.Values.FirstOrDefault(session =>
                session.FlowKind == flowKind
                && session.ProviderKey == providerKey
                && (session.ProviderSessionId == providerSessionId
                    || session.ProviderCorrelationId == providerSessionId));
            return Task.FromResult(new AZOAResult<HostedSession>
            {
                Message = found is null ? "No hosted session found." : "Success",
                Result = found is null ? null : Clone(found),
            });
        }
    }

    public Task<AZOAResult<HostedSession>> GetByAdmissionAsync(string flowKind, string providerKey, string admissionKey, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var found = _sessions.Values.FirstOrDefault(row => row.FlowKind == flowKind && row.ProviderKey == providerKey && row.AdmissionKey == admissionKey);
            return Task.FromResult(AZOAResult<HostedSession>.Success(found is null ? null! : Clone(found)));
        }
    }

    public Task<AZOAResult<HostedSession>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _sessions.TryGetValue(id.ToString("N"), out var found);
            return Task.FromResult(new AZOAResult<HostedSession>
            {
                Message = found is null ? "No hosted session found." : "Success",
                Result = found is null ? null : Clone(found),
            });
        }
    }

    public Task<AZOAResult<HostedSession>> TryAttachProviderSessionAsync(
        Guid id,
        long expectedStateVersion,
        string providerSessionId,
        string? providerCorrelationId,
        string redirectUrl,
        DateTimeOffset? expiresAt,
        string creationToken,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            // Mirrors the store: caller-version CAS (a quarantine may intervene), CLAIMED, creator token, creation started.
            if (!_sessions.TryGetValue(id.ToString("N"), out var session)
                || (session.StateVersion != expectedStateVersion && session.CreationQuarantinedAt is null)
                || session.State != HostedSessionStates.Claimed || session.ProviderSessionId is not null
                || session.CreationToken != creationToken || session.CreationStartedAt is null)
            {
                return Task.FromResult(new AZOAResult<HostedSession>
                {
                    Message = "Hosted session admission changed.",
                    Result = null,
                });
            }

            session.ProviderSessionId = providerSessionId;
            session.ProviderCorrelationId = providerCorrelationId;
            session.RedirectUrl = redirectUrl;
            session.ExpiresAt = expiresAt;
            session.State = HostedSessionStates.AwaitingSubject;
            session.StateVersion++;
            return Task.FromResult(AZOAResult<HostedSession>.Success(Clone(session), "Attached."));
        }
    }

    public Task<AZOAResult<HostedSession>> TryBeginCreationAsync(Guid id, string creationToken, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id.ToString("N"), out var row)
                || row.State != HostedSessionStates.Claimed
                || row.CreationToken != creationToken || row.CreationStartedAt is not null
                || row.CreationQuarantinedAt is not null || row.ProviderSessionId is not null)
                return Task.FromResult(AZOAResult<HostedSession>.Success(null!));
            row.CreationStartedAt = now;
            row.StateVersion++;
            return Task.FromResult(AZOAResult<HostedSession>.Success(Clone(row)));
        }
    }

    public Task<bool> ReleaseUnsentAdmissionAsync(Guid id, string creationToken, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id.ToString("N"), out var row)
                || row.State != HostedSessionStates.Claimed || row.CreationToken != creationToken
                || row.ProviderSessionId is not null) return Task.FromResult(false);
            row.State = HostedSessionStates.Abandoned;
            row.StateVersion++;
            row.AdmissionKey += ":resolved:" + row.Id;
            row.CreationStartedAt = null;
            row.CreationQuarantinedAt = null;
            row.CompletedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<HostedSession>> ListReconciliationAsync(DateTimeOffset before, int limit, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<HostedSession>>(_sessions.Values
                .Where(row => row.State == HostedSessionStates.Claimed && row.ProviderSessionId is null && (row.CreationClaimedAt <= before || (row.CreationClaimedAt == null && row.CreatedDate <= before)))
                .OrderBy(row => row.CreatedDate).Take(limit).Select(Clone).ToArray());
    }

    public Task<AZOAResult<HostedSession>> ReconcileAdmissionAsync(
        HostedSession expected, Guid actorId, string reason, HostedSessionCreation? providerSession,
        DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(expected.Id, out var row)
                || row.State != HostedSessionStates.Claimed || row.StateVersion != expected.StateVersion
                || row.ProviderSessionId is not null || (row.CreationClaimedAt ?? row.CreatedDate) > now.AddMinutes(-5))
                return Task.FromResult(AZOAResult<HostedSession>.Success(null!));
            row.StateVersion++;
            row.ReconciledAt = now;
            row.ReconciledByUserId = actorId.ToString("N");
            row.ReconciliationReason = reason;
            if (providerSession is null)
            {
                if (row.CreationStartedAt is null && !string.IsNullOrWhiteSpace(row.CreationToken))
                {
                    row.State = HostedSessionStates.Abandoned;
                    row.AdmissionKey += ":resolved:" + row.Id;
                }
                else row.CreationQuarantinedAt = now;
            }
            else
            {
                row.State = HostedSessionStates.AwaitingSubject;
                row.ProviderSessionId = providerSession.ProviderSessionId;
                row.ProviderCorrelationId = providerSession.ProviderCorrelationId;
                row.RedirectUrl = providerSession.RedirectUrl;
            }
            return Task.FromResult(AZOAResult<HostedSession>.Success(Clone(row)));
        }
    }

    public Task<AZOAResult<HostedSession>> TryClaimResumeAsync(
        Guid id, long expectedStateVersion, string token, DateTimeOffset now,
        DateTimeOffset leaseExpiresAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id.ToString("N"), out var session)
                || session.StateVersion != expectedStateVersion
                || HostedSessionStates.IsTerminal(session.State)
                || (session.State == HostedSessionStates.Resuming && session.ResumeLeaseExpiresAt > now))
                return Task.FromResult(AZOAResult<HostedSession>.Success(null!));
            session.State = HostedSessionStates.Resuming;
            session.StateVersion++;
            session.ResumeLeaseToken = token;
            session.ResumeLeaseExpiresAt = leaseExpiresAt;
            return Task.FromResult(AZOAResult<HostedSession>.Success(Clone(session)));
        }
    }

    public Task<bool> RenewResumeLeaseAsync(
        Guid id, string token, DateTimeOffset now, DateTimeOffset leaseExpiresAt,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id.ToString("N"), out var session)
                || session.State != HostedSessionStates.Resuming
                || session.ResumeLeaseToken != token || session.ResumeLeaseExpiresAt <= now)
                return Task.FromResult(false);
            session.ResumeLeaseExpiresAt = leaseExpiresAt;
            return Task.FromResult(true);
        }
    }

    public Task<AZOAResult<HostedSession>> TryFinishResumeAsync(
        Guid id, long expectedStateVersion, string token, DateTimeOffset now,
        string newState, string? authoritativeOutcome, string? outcomeDetail,
        string? resumeTargetId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id.ToString("N"), out var session)
                || session.StateVersion != expectedStateVersion || session.State != HostedSessionStates.Resuming
                || session.ResumeLeaseToken != token || session.ResumeLeaseExpiresAt <= now)
                return Task.FromResult(AZOAResult<HostedSession>.Success(null!));
            session.State = newState;
            session.StateVersion++;
            session.AuthoritativeOutcome = authoritativeOutcome;
            session.OutcomeDetail = outcomeDetail;
            session.ResumeTargetId = resumeTargetId;
            session.ResumeLeaseToken = null;
            session.ResumeLeaseExpiresAt = null;
            if (HostedSessionStates.IsTerminal(newState)) TerminalWriteCount++;
            return Task.FromResult(AZOAResult<HostedSession>.Success(Clone(session)));
        }
    }

    public Task<AZOAResult<HostedSession>> TryTransitionAsync(
        Guid id,
        long expectedStateVersion,
        string newState,
        string? authoritativeOutcome,
        string? outcomeDetail,
        string? resumeTargetId,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id.ToString("N"), out var session)
                || session.StateVersion != expectedStateVersion
                || HostedSessionStates.IsTerminal(session.State)
                || session.State == HostedSessionStates.Resuming)
            {
                // The loser of a race gets a null result, not an error.
                return Task.FromResult(new AZOAResult<HostedSession>
                {
                    Message = "Hosted session already transitioned.",
                    Result = null,
                });
            }

            session.State = newState;
            session.StateVersion++;
            session.AuthoritativeOutcome = authoritativeOutcome;
            session.OutcomeDetail = outcomeDetail;
            session.ResumeTargetId = resumeTargetId;
            if (HostedSessionStates.IsTerminal(newState))
            {
                session.CompletedAt = DateTimeOffset.UtcNow;
                _terminalWriteCount++;
            }

            return Task.FromResult(AZOAResult<HostedSession>.Success(Clone(session), "Transitioned."));
        }
    }

    public Task<AZOAResult<bool>> RecordCallbackAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _callbackRecordCount++;
            if (_sessions.TryGetValue(id.ToString("N"), out var session))
            {
                session.CallbackCount++;
                session.LastCallbackAt = DateTimeOffset.UtcNow;
            }

            return Task.FromResult(AZOAResult<bool>.Success(true));
        }
    }

    /// <summary>Seeds a session already awaiting its subject, as after a begin.</summary>
    public HostedSession SeedAwaitingSubject(
        string flowKind,
        string providerKey,
        string providerSessionId,
        Guid subjectUserId,
        Guid? tenantId = null)
    {
        var session = new HostedSession
        {
            Id = Guid.NewGuid().ToString("N"),
            FlowKind = flowKind,
            ProviderKey = providerKey,
            AdmissionKey = $"seed:{providerSessionId}",
            SubjectUserId = subjectUserId.ToString("N"),
            TenantId = tenantId?.ToString("N"),
            ProviderSessionId = providerSessionId,
            RedirectUrl = "https://widget.identomat.com/?session_token=" + providerSessionId,
            State = HostedSessionStates.AwaitingSubject,
            StateVersion = 1,
            CreatedDate = DateTimeOffset.UtcNow,
        };
        lock (_gate)
        {
            _sessions[session.Id] = session;
            return Clone(session);
        }
    }

    /// <summary>Current stored state, bypassing the conditional read paths.</summary>
    public HostedSession? Peek(Guid id)
    {
        lock (_gate)
            return _sessions.TryGetValue(id.ToString("N"), out var session) ? Clone(session) : null;
    }

    private static HostedSession Clone(HostedSession session) => new()
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
        ReconciliationAuditJson = session.ReconciliationAuditJson,
        SubjectUserId = session.SubjectUserId,
        TenantId = session.TenantId,
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
