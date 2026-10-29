using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>Current authority boundaries for tenant-owned custody reads and admissions.</summary>
public interface ITenantCustodialAccess
{
    /// <summary>Captures the actual credential and exact current member authority for the requested scope.</summary>
    /// <remarks>A tenant-linked member needs the open membership and implied grant; a claimed member needs a live UserExplicit grant.</remarks>
    Task<TenantCustodialWitness?> CaptureAsync(Guid userId, string externalUserId, SigningConsentWitness authority,
        string requiredScope, CancellationToken ct = default);

    /// <summary>Rechecks the captured authority atomically at the consuming response boundary.</summary>
    Task<bool> ValidateAsync(TenantCustodialWitness witness, string requiredScope, CancellationToken ct = default);

    /// <summary>Whether the member is still linked to <paramref name="tenantId"/>, claimed from it, or not its member (null).</summary>
    /// <remarks>Grants nothing: callers use it only to choose a projection, never to authorize a read.</remarks>
    Task<TenantCustodialKind?> ResolveOwnershipAsync(Guid userId, string externalUserId, Guid tenantId, CancellationToken ct = default);
}
