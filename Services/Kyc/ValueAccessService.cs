// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Kyc;

/// <summary>
/// Value-path KYC check over the host's <see cref="IRealValueKycGate"/> (the reversal risk hold wrapping the
/// KYC gate), never the bare <see cref="IKycGateService"/>. See <c>Services/Kyc/AGENTS.md</c> §value gate.
/// </summary>
public sealed class ValueAccessService(IRealValueKycGate valueGate) : IValueAccessService
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
            ? valueGate.RequireCurrentApprovalAsync(participantId, tenantId.Value, ct)
            : valueGate.RequireCurrentApprovalAsync(participantId, ct);
    }
}
