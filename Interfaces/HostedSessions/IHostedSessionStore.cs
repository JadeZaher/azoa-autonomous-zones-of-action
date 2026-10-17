// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.HostedSessions;

/// <summary>
/// Durable admission + CAS terminal-state persistence for the hosted-session flow.
/// Every transition is conditional so a redelivered callback loses instead of
/// producing a second terminal write.
/// </summary>
public interface IHostedSessionStore
{
    /// <summary>
    /// Claims admission for <c>(flowKind, providerKey, admissionKey)</c>. When an
    /// unexpired claim already exists it is returned instead, so a retry resumes
    /// rather than creating a second provider session.
    /// </summary>
    Task<AZOAResult<HostedSession>> ClaimAdmissionAsync(
        HostedSession candidate,
        CancellationToken ct = default);

    /// <summary>Releases an owned admission after its creator confirms no request was sent, preserving its audit record.</summary>
    Task<bool> ReleaseUnsentAdmissionAsync(Guid id, string creationToken, CancellationToken ct = default);

    /// <summary>Fences the sole creator before any provider request can be sent.</summary>
    Task<AZOAResult<HostedSession>> TryBeginCreationAsync(Guid id, string creationToken, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Lists aged unattached admissions for operator reconciliation without exposing provider credentials.</summary>
    Task<IReadOnlyList<HostedSession>> ListReconciliationAsync(DateTimeOffset before, int limit, CancellationToken ct = default);

    /// <summary>Audits verified attachment or absence; only provably unsent admissions release, uncertain sends quarantine; stale versions return no row.</summary>
    Task<AZOAResult<HostedSession>> ReconcileAdmissionAsync(
        HostedSession expected, Guid actorId, string reason,
        AZOA.WebAPI.Models.HostedSessions.HostedSessionCreation? providerSession,
        DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Looks a session up by its provider-side session id.</summary>
    Task<AZOAResult<HostedSession>> GetByProviderSessionAsync(
        string flowKind,
        string providerKey,
        string providerSessionId,
        CancellationToken ct = default);

    Task<AZOAResult<HostedSession>> GetByAdmissionAsync(string flowKind, string providerKey, string admissionKey, CancellationToken ct = default);

    Task<AZOAResult<HostedSession>> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Attaches the provider session created after admission: compare-and-swap on
    /// <paramref name="expectedStateVersion"/> plus <c>CLAIMED</c>, the creator token and a begun creation.
    /// The one tolerated intervening write is an operator quarantine, which a late original response resolves.
    /// </summary>
    Task<AZOAResult<HostedSession>> TryAttachProviderSessionAsync(
        Guid id,
        long expectedStateVersion,
        string providerSessionId,
        string? providerCorrelationId,
        string redirectUrl,
        DateTimeOffset? expiresAt,
        string creationToken,
        CancellationToken ct = default);

    /// <summary>
    /// Compare-and-swap transition. Returns the updated row only for the caller that
    /// matched <paramref name="expectedStateVersion"/> on a NON-TERMINAL row; every
    /// other concurrent or redelivered caller receives a null result.
    /// </summary>
    Task<AZOAResult<HostedSession>> TryTransitionAsync(
        Guid id,
        long expectedStateVersion,
        string newState,
        string? authoritativeOutcome,
        string? outcomeDetail,
        string? resumeTargetId,
        CancellationToken ct = default);

    /// <summary>Claims resumption at the observed version when no live lease exists; contention returns no row.</summary>
    Task<AZOAResult<HostedSession>> TryClaimResumeAsync(
        Guid id, long expectedStateVersion, string token, DateTimeOffset now,
        DateTimeOffset leaseExpiresAt, CancellationToken ct = default);

    /// <summary>Extends an unexpired lease owned by this token; a lost lease returns false.</summary>
    Task<bool> RenewResumeLeaseAsync(
        Guid id, string token, DateTimeOffset now, DateTimeOffset leaseExpiresAt,
        CancellationToken ct = default);

    /// <summary>Transitions and releases an unexpired owned lease; stale ownership returns no row.</summary>
    Task<AZOAResult<HostedSession>> TryFinishResumeAsync(
        Guid id, long expectedStateVersion, string token, DateTimeOffset now,
        string newState, string? authoritativeOutcome, string? outcomeDetail,
        string? resumeTargetId, CancellationToken ct = default);

    /// <summary>Records an accepted callback delivery without deciding anything.</summary>
    Task<AZOAResult<bool>> RecordCallbackAsync(Guid id, CancellationToken ct = default);
}
