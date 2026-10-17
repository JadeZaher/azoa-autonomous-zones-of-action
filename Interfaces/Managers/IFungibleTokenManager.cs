using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>Admits immutable fungible creation for an authenticated owner without signing before quote acceptance.</summary>
public interface IFungibleTokenManager
{
    /// <summary>Returns the original keyed operation; AssetId is populated only after authoritative confirmation and atomic projection.</summary>
    Task<AZOAResult<FungibleTokenResult>> CreateAsync(Guid userId, FungibleTokenCreateRequest request,
        Guid callerUserId, string? clientIdempotencyKey, string apiKeyId, Guid? actingTenantId = null);
}
