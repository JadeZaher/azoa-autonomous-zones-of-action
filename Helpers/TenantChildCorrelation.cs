using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Helpers;

/// <summary>Immutable tenant-partitioned member identity that survives an account claim; see Helpers/AGENTS.md §tenant-child-correlation.</summary>
public static class TenantChildCorrelation
{
    /// <summary>SHA-256 over the v1 (tenant, external user) correlation input.</summary>
    public static byte[] CorrelationHash(Guid tenantId, string externalUserId)
        => SHA256.HashData(Encoding.UTF8.GetBytes($"azoa:tenant-user:v1:{tenantId:N}:{externalUserId}"));

    /// <summary>The one user id a tenant's external user can ever be provisioned under.</summary>
    public static Guid DeterministicUserId(Guid tenantId, string externalUserId)
        => new(CorrelationHash(tenantId, externalUserId).AsSpan(0, 16));

    /// <summary>True while the tenant link is set, or after a claim when the id is still the tenant's deterministic id.</summary>
    public static bool IsCorrelated(IUser user, Guid tenantId)
        => IsCorrelated(user.Id, user.OwnerTenantId, user.ExternalUserId, user.AuthNotBefore is not null, tenantId);

    /// <inheritdoc cref="IsCorrelated(IUser, Guid)"/>
    public static bool IsCorrelated(Guid userId, Guid? ownerTenantId, string? externalUserId, bool claimWatermarked, Guid tenantId)
        => ownerTenantId is { } owner ? owner == tenantId : IsClaimed(userId, ownerTenantId, externalUserId, claimWatermarked, tenantId);

    /// <summary>True only for a self-owned account the claim stamped (AuthNotBefore set) whose id is the deterministic id of (tenant, external user).</summary>
    public static bool IsClaimed(Guid userId, Guid? ownerTenantId, string? externalUserId, bool claimWatermarked, Guid tenantId)
        => ownerTenantId is null && claimWatermarked && tenantId != Guid.Empty && userId != Guid.Empty
            && !string.IsNullOrWhiteSpace(externalUserId) && userId == DeterministicUserId(tenantId, externalUserId);
}
