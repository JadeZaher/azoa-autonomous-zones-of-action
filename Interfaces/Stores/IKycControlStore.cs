using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Exclusive descending position in the immutable KYC control audit ordering.</summary>
public sealed record KycControlAuditCursor(DateTimeOffset OccurredAt, string RecordId);

/// <summary>Versioned, audited persistence for node KYC profiles and tenant selections.</summary>
public interface IKycControlStore
{
    Task<AZOAResult<IReadOnlyList<KycProviderProfile>>> ListProfilesAsync(CancellationToken ct = default);

    Task<AZOAResult<KycProviderProfile?>> GetProfileAsync(string providerKey, CancellationToken ct = default);

    Task<AZOAResult<KycProviderProfile>> SaveProfileAsync(
        KycProviderProfile profile,
        KycControlAudit audit,
        long? expectedVersion,
        bool retireActiveAttempts,
        CancellationToken ct = default);

    Task<AZOAResult<TenantKycProviderSelection?>> GetSelectionAsync(
        Guid tenantId,
        CancellationToken ct = default);

    Task<AZOAResult<TenantKycProviderSelection>> SaveSelectionAsync(
        TenantKycProviderSelection selection,
        KycControlAudit audit,
        long? expectedVersion,
        CancellationToken ct = default);

    Task<AZOAResult<IReadOnlyList<TenantKycProviderSelection>>> ListSelectionsPageAsync(
        int offset,
        int limit,
        string? search,
        CancellationToken ct = default);

    Task<AZOAResult<long>> CountSelectionsAsync(CancellationToken ct = default);

    Task<AZOAResult<IReadOnlyList<KycControlAudit>>> ListAuditPageAsync(
        int limit,
        KycControlAuditCursor? before,
        Guid? tenantId,
        string? providerKey,
        string? action,
        CancellationToken ct = default);
}
