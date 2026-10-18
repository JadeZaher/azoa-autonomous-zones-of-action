using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>Authorization-safe operator and tenant KYC configuration orchestration.</summary>
public interface IKycControlPlaneManager
{
    Task<AZOAResult<IReadOnlyList<KycProviderProfileResponse>>> ListProfilesAsync(CancellationToken ct = default);
    Task<AZOAResult<KycProviderProfileResponse>> UpdateProfileAsync(
        string providerKey,
        UpdateKycProviderProfileRequest request,
        Guid operatorUserId,
        OperatorActor? actor = null,
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
        Guid actorUserId,
        bool requireTenantAuthority,
        OperatorActor? actor = null,
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
        Guid operatorUserId,
        CancellationToken ct = default);
    /// <summary>Operator queue: verified people whose AML verdict still owes a human decision.</summary>
    Task<AZOAResult<CursorPage<AmlManualReviewItem>>> ListAmlReviewsAsync(
        int limit,
        string? cursor,
        Guid operatorUserId,
        CancellationToken ct = default);

    /// <summary>Records an operator AML decision as a superseding screening row.</summary>
    Task<AZOAResult<AmlManualReviewItem>> DecideAmlReviewAsync(
        Guid subjectUserId,
        AmlManualReviewDecisionRequest request,
        Guid operatorUserId,
        OperatorActor? actor = null,
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
