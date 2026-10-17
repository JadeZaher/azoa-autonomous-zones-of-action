using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Admin;

namespace AZOA.WebAPI.Helpers;

/// <summary>Checks current tenant ownership through active users and provisioning credentials.</summary>
public static class TenantAuthority
{
    public static async Task<AZOAResult<bool>> CheckAsync(Guid tenantId, IUserStore users, IApiKeyStore apiKeys,
        DateTimeOffset now, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || tenantId == NodeOperatorIdentity.UserId)
            return AZOAResult<bool>.Success(false);
        var user = await users.GetByIdAsync(tenantId, ct);
        if (user.IsError)
            return user.Code == AzoaErrorCodes.NotFound ? AZOAResult<bool>.Success(false)
                : AZOAResult<bool>.FailureWithCode("Tenant authority is unavailable.", AzoaErrorCodes.DependencyUnavailable);
        if (user.Result is null || !user.Result.IsActive) return AZOAResult<bool>.Success(false);
        var keys = await apiKeys.ListByUserAsync(tenantId, ct);
        return AZOAResult<bool>.Success(keys.Any(key => key.IsActive && key.RevokedAt is null
            && (key.ExpiresAt is null || key.ExpiresAt > now.UtcDateTime)
            && (key.Scopes ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(AzoaScopes.TenantProvision, StringComparer.Ordinal)));
    }
}
