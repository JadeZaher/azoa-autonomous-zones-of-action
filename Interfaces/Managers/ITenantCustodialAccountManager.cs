using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>Retryable tenant-custody result discriminators translated to HTTP 409.</summary>
public static class TenantCustodialOperationError
{
    public const string CustodyInProgress = "TENANT_CUSTODY_IN_PROGRESS: ";
    public const string KycSessionInProgress = "KYC_SESSION_IN_PROGRESS: ";
}

/// <summary>Tenant-scoped orchestration over the existing avatar, wallet, and KYC aggregates.</summary>
public interface ITenantCustodialAccountManager
{
    TenantCustodialCapabilitiesResponse GetCapabilities();

    Task<AZOAResult<TenantCustodialCapabilitiesResponse>> GetCapabilitiesAsync(
        Guid tenantId,
        CancellationToken ct = default);

    Task<AZOAResult<TenantCustodialAccountStatusResponse>> EnsureAsync(
        Guid tenantId,
        string externalSubject,
        string idempotencyKey,
        CancellationToken ct = default);

    Task<AZOAResult<TenantCustodialAccountStatusResponse>> GetStatusAsync(
        Guid tenantId,
        string externalSubject,
        CancellationToken ct = default);

    Task<AZOAResult<TenantKycSessionResponse>> BeginKycAsync(
        Guid tenantId,
        string externalSubject,
        string idempotencyKey,
        CancellationToken ct = default);

    Task<AZOAResult<TenantKycSubmissionResponse>> SubmitKycAsync(
        Guid tenantId,
        string externalSubject,
        TenantKycSubmissionRequest request,
        CancellationToken ct = default);
}
