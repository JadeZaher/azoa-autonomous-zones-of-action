using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Managers;

/// <inheritdoc/>
public sealed class ConsentManager(IConsentGrantStore grants) : IConsentManager
{
    private readonly IConsentGrantStore _grants = grants;

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<ConsentGrant>>> ListForUserAsync(Guid grantorUserId, CancellationToken ct = default)
    {
        // Store query is grantor-scoped — a cross-user probe yields an empty list,
        // never another user's grants (AC1).
        var listed = await _grants.ListByGrantorAsync(grantorUserId, ct);
        return Project(listed);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<ConsentGrant>> GetForUserAsync(Guid grantorUserId, Guid grantId, CancellationToken ct = default)
    {
        var result = new AZOAResult<ConsentGrant>();

        var loaded = await _grants.GetByIdAsync(grantId, ct);

        // Cross-user reads are indistinguishable from a missing grant.
        if (loaded.IsError || loaded.Result is null
            || loaded.Result.GrantorUserId != grantorUserId)
        {
            result.IsError = true;
            result.Message = TenantAuthorizationError.NotFound + "No such consent grant.";
            return result;
        }

        result.Result = loaded.Result;
        result.Message = "Success";
        return result;
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<ConsentGrant>>> ListForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        // L2: store query is tenant-scoped — a tenant can never receive another
        // tenant's or a non-grantee's grants.
        var listed = await _grants.ListByTenantAsync(tenantId, ct);
        return Project(listed);
    }

    private static AZOAResult<IEnumerable<ConsentGrant>> Project(AZOAResult<IEnumerable<ConsentGrant>> listed)
    {
        var result = new AZOAResult<IEnumerable<ConsentGrant>>();
        if (listed.IsError)
        {
            result.IsError = true;
            result.Message = listed.Message;
            result.Exception = listed.Exception;
            return result;
        }

        result.Result = (listed.Result ?? Enumerable.Empty<ConsentGrant>()).ToList();
        result.Message = "Success";
        return result;
    }
}
