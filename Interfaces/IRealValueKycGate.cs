using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

/// <summary>Authorizes a real-value operation from Azoa's KYC ledger.</summary>
public interface IRealValueKycGate
{
    /// <summary>
    /// Succeeds only when <paramref name="avatarId"/>'s latest authoritative
    /// submission is approved and has an explicit expiry later than the current
    /// time. Missing, indefinite, expired, revoked, or unreadable approvals fail
    /// closed. Callers must enforce this before any claim, state transition, or
    /// external value effect.
    /// </summary>
    Task<AZOAResult<bool>> RequireCurrentApprovalAsync(
        Guid avatarId,
        CancellationToken ct = default);

    Task<AZOAResult<bool>> RequireCurrentApprovalAsync(
        Guid avatarId,
        Guid tenantId,
        CancellationToken ct = default);
}
