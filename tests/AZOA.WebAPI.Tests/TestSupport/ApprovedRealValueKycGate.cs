using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.TestSupport;

internal sealed class ApprovedRealValueKycGate : IRealValueKycGate
{
    public Task<AZOAResult<bool>> RequireCurrentApprovalAsync(
        Guid userId,
        CancellationToken ct = default)
        => Task.FromResult(AZOAResult<bool>.Success(true));

    public Task<AZOAResult<bool>> RequireCurrentApprovalAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken ct = default)
        => Task.FromResult(AZOAResult<bool>.Success(true));
}
