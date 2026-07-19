// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Kyc;

/// <inheritdoc/>
public sealed class ValueAccessService(IKycGateService kycGate) : IValueAccessService
{
    /// <inheritdoc/>
    public async Task<ValueAccessDecision> GetDecisionAsync(
        Guid participantId,
        Guid? tenantId = null,
        CancellationToken ct = default)
    {
        var result = await RequireValueAccessAsync(participantId, tenantId, ct);
        return new ValueAccessDecision(
            result.IsError ? ValueAccessState.VerificationRequired : ValueAccessState.Ready);
    }

    /// <inheritdoc/>
    public Task<AZOAResult<bool>> RequireValueAccessAsync(
        Guid participantId,
        Guid? tenantId = null,
        CancellationToken ct = default)
    {
        if (participantId == Guid.Empty)
            return Task.FromResult(AZOAResult<bool>.Failure(
                KycAuthorizationError.Forbidden + KycAuthorizationError.VerificationRequiredMessage));

        return tenantId.HasValue
            ? kycGate.RequireVerifiedAsync(participantId, tenantId.Value, ct)
            : kycGate.RequireVerifiedAsync(participantId);
    }
}
