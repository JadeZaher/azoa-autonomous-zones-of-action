using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Settings;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Providers;

public sealed record KycProviderResolution(
    IKycProviderService Provider,
    KycSettings Settings,
    Guid? TenantId,
    long SelectionVersion,
    long TrustRevision,
    string DisplayName);

/// <summary>Resolves the exact provider and policy authority for a tenant KYC attempt.</summary>
public interface IKycProviderRegistry
{
    Task<AZOAResult<KycProviderResolution>> ResolveTenantAsync(
        Guid tenantId,
        CancellationToken ct = default);

    AZOAResult<KycProviderResolution> ResolveNodeDefault();

    Task<AZOAResult<IReadOnlyList<KycProviderProfileResponse>>> ListProfilesAsync(
        CancellationToken ct = default);

    Task<AZOAResult<IReadOnlyList<TenantKycProviderChoiceResponse>>> ListTenantChoicesAsync(
        CancellationToken ct = default);

    Task<AZOAResult<KycProviderProfileResponse>> EvaluateProfileAsync(
        string providerKey,
        CancellationToken ct = default);

    KycProviderProfileResponse EvaluateCandidate(KycProviderProfile profile);
}
