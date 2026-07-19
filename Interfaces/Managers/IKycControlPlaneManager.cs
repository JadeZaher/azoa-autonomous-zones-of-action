using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>Authorization-safe operator and tenant KYC configuration orchestration.</summary>
public interface IKycControlPlaneManager
{
    Task<AZOAResult<IReadOnlyList<KycProviderProfileResponse>>> ListProfilesAsync(CancellationToken ct = default);
    Task<AZOAResult<KycProviderProfileResponse>> UpdateProfileAsync(
        string providerKey,
        UpdateKycProviderProfileRequest request,
        Guid operatorAvatarId,
        CancellationToken ct = default);
    Task<AZOAResult<IReadOnlyList<TenantKycProviderChoiceResponse>>> ListTenantChoicesAsync(
        Guid tenantId,
        CancellationToken ct = default);
    Task<AZOAResult<TenantKycSelectionResponse>> GetTenantSelectionAsync(
        Guid tenantId,
        bool requireTenantAuthority,
        CancellationToken ct = default);
    Task<AZOAResult<TenantKycSelectionResponse>> SelectTenantProviderAsync(
        Guid tenantId,
        SelectTenantKycProviderRequest request,
        Guid actorAvatarId,
        bool requireTenantAuthority,
        CancellationToken ct = default);
    Task<AZOAResult<CursorPage<OperatorTenantKycSummaryResponse>>> ListTenantsAsync(
        int limit,
        string? cursor,
        string? search,
        CancellationToken ct = default);
    Task<AZOAResult<CursorPage<OperatorKycSubmissionQueueItem>>> ListQueueAsync(
        string status,
        int limit,
        string? cursor,
        CancellationToken ct = default);
    Task<AZOAResult<OperatorKycSubmissionQueueItem>> DecideAsync(
        Guid submissionId,
        OperatorKycDecisionRequest request,
        Guid operatorAvatarId,
        CancellationToken ct = default);
    Task<AZOAResult<NodeOperatorOverviewResponse>> GetOverviewAsync(CancellationToken ct = default);
    Task<AZOAResult<CursorPage<KycControlAuditResponse>>> ListAuditAsync(
        int limit,
        string? cursor,
        Guid? tenantId,
        string? providerKey,
        string? action,
        CancellationToken ct = default);
}
