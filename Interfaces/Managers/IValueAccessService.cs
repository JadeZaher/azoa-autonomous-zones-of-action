// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>Authoritative server-side readiness decision for value-bearing actions.</summary>
public interface IValueAccessService
{
    /// <summary>Returns the non-sensitive readiness state for a participant.</summary>
    Task<ValueAccessDecision> GetDecisionAsync(
        Guid participantId,
        Guid? tenantId = null,
        CancellationToken ct = default);

    /// <summary>Fails closed with the established KYC authorization error when value access is unavailable.</summary>
    Task<AZOAResult<bool>> RequireValueAccessAsync(
        Guid participantId,
        Guid? tenantId = null,
        CancellationToken ct = default);
}
