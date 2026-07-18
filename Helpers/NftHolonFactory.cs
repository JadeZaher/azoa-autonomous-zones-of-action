using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Helpers;

/// <summary>Builds the common NFT metadata Holon for guarded mint flows.</summary>
public static class NftHolonFactory
{
    /// <summary>Creates an active NFT Holon from a validated mint request.</summary>
    public static Holon Create(NftMintRequest request, Guid avatarId)
    {
        var metadata = new Dictionary<string, string>(request.Metadata);
        if (!string.IsNullOrEmpty(request.ImageUri)) metadata["image"] = request.ImageUri;
        if (!string.IsNullOrEmpty(request.ExternalUri)) metadata["external_url"] = request.ExternalUri;

        return new Holon
        {
            AvatarId = avatarId,
            Name = request.Name,
            Description = request.Description,
            AssetType = "NFT",
            ChainId = request.ChainId,
            TokenId = request.TokenId,
            Metadata = metadata,
            ProviderName = "PostgreSQL",
            IsActive = true,
        };
    }
}
