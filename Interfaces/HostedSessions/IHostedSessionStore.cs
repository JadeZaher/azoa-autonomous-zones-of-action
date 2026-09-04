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

    /// <summary>Looks a session up by its provider-side session id.</summary>
    Task<AZOAResult<HostedSession>> GetByProviderSessionAsync(
        string flowKind,
        string providerKey,
        string providerSessionId,
        CancellationToken ct = default);

    Task<AZOAResult<HostedSession>> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Attaches the provider session created after admission. Conditional on the
    /// session still being in <c>CLAIMED</c> at the expected version.
    /// </summary>
    Task<AZOAResult<HostedSession>> TryAttachProviderSessionAsync(
        Guid id,
        long expectedStateVersion,
        string providerSessionId,
        string? providerCorrelationId,
        string redirectUrl,
        DateTimeOffset? expiresAt,
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

    /// <summary>Records an accepted callback delivery without deciding anything.</summary>
    Task<AZOAResult<bool>> RecordCallbackAsync(Guid id, CancellationToken ct = default);
}
