using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Kyb;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>KYB gate for unit tests whose subject is not the D-6 decision itself; every tenant is verified.</summary>
public sealed class ApprovedKybGate : IKybGate
{
    public Task<AZOAResult<bool>> RequireVerifiedTenantAsync(Guid tenantId, KybCapability capability, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<bool>.Success(true));
}
