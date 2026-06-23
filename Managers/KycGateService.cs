// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Managers;

/// <summary>
/// Reads the KYC ledger (<see cref="IKycStore"/>) to answer the gate questions.
/// The avatar is "verified" iff its most-recent submission is APPROVED — so a
/// later REJECTED/PENDING re-submission correctly closes the gate again. No
/// avatar-table column is consulted (D3).
/// </summary>
public sealed class KycGateService : IKycGateService
{
    private readonly IKycStore _store;

    public KycGateService(IKycStore store)
    {
        _store = store;
    }

    public async Task<AZOAResult<bool>> RequireVerifiedAsync(Guid avatarId)
    {
        var latest = await _store.GetLatestSubmissionByAvatarAsync(avatarId);
        if (latest.IsError)
            return new AZOAResult<bool> { IsError = true, Result = false, Message = latest.Message, Exception = latest.Exception };

        if (latest.Result is { Status: KycStatus.APPROVED })
            return new AZOAResult<bool> { Result = true, Message = "Success" };

        // No submission, or the latest is not APPROVED — gate closed.
        return new AZOAResult<bool>
        {
            IsError = true,
            Result  = false,
            Message = $"{KycAuthorizationError.Forbidden}{KycAuthorizationError.VerificationRequiredMessage}"
        };
    }

    public async Task<AZOAResult<KycStatus>> GetKycStatusAsync(Guid avatarId)
    {
        var latest = await _store.GetLatestSubmissionByAvatarAsync(avatarId);
        if (latest.IsError)
            return new AZOAResult<KycStatus> { IsError = true, Message = latest.Message, Exception = latest.Exception };

        if (latest.Result is null)
            return new AZOAResult<KycStatus>
            {
                IsError = true,
                Message = $"{KycAuthorizationError.NotFound}No KYC submission found for this avatar."
            };

        return new AZOAResult<KycStatus> { Result = latest.Result.Status, Message = "Success" };
    }
}
