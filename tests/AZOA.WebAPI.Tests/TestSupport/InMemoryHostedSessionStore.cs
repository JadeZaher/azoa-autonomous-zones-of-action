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
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            // Mirrors the SurrealQL WHERE: CLAIMED or AWAITING_SUBJECT, exact version.
            if (!_sessions.TryGetValue(id.ToString("N"), out var session)
                || (session.State != HostedSessionStates.Claimed
                    && session.State != HostedSessionStates.AwaitingSubject)
                || session.StateVersion != expectedStateVersion)
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
                || HostedSessionStates.IsTerminal(session.State))
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
        Guid subjectAvatarId,
        Guid? tenantId = null)
    {
        var session = new HostedSession
        {
            Id = Guid.NewGuid().ToString("N"),
            FlowKind = flowKind,
            ProviderKey = providerKey,
            AdmissionKey = $"seed:{providerSessionId}",
            SubjectAvatarId = subjectAvatarId.ToString("N"),
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
        SubjectAvatarId = session.SubjectAvatarId,
        TenantId = session.TenantId,
        ProviderSessionId = session.ProviderSessionId,
        ProviderCorrelationId = session.ProviderCorrelationId,
        RedirectUrl = session.RedirectUrl,
        State = session.State,
        StateVersion = session.StateVersion,
        AuthoritativeOutcome = session.AuthoritativeOutcome,
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
