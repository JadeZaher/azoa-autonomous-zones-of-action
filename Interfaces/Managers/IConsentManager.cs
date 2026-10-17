using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>Scoped consent reads; mutations use the atomic consent lifecycle service.</summary>
public interface IConsentManager
{
    /// <summary>Lists only grants belonging to the authenticated owner.</summary>
    Task<AZOAResult<IEnumerable<ConsentGrant>>> ListForUserAsync(Guid grantorUserId, CancellationToken ct = default);
    /// <summary>Reads one owned grant, hiding foreign records as missing.</summary>
    Task<AZOAResult<ConsentGrant>> GetForUserAsync(Guid grantorUserId, Guid grantId, CancellationToken ct = default);
    /// <summary>Lists only grants made to the authenticated tenant.</summary>
    Task<AZOAResult<IEnumerable<ConsentGrant>>> ListForTenantAsync(Guid tenantId, CancellationToken ct = default);
}
