using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>Current authority boundaries for tenant-owned custody reads and admissions.</summary>
public interface ITenantCustodialAccess
{
    /// <summary>Captures the actual credential and exact current enrollment for the requested scope.</summary>
    Task<TenantCustodialWitness?> CaptureAsync(Guid userId, string externalUserId, SigningConsentWitness authority,
        string requiredScope, CancellationToken ct = default);

    /// <summary>Rechecks the captured authority atomically at the consuming response boundary.</summary>
    Task<bool> ValidateAsync(TenantCustodialWitness witness, string requiredScope, CancellationToken ct = default);
}
