using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Helpers;

/// <summary>Copies mutable local entities without losing custody fields or sharing metadata.</summary>
public static class ActionLocalSnapshot
{
    public static Holon Copy(IHolon value) => new()
    {
        Id = value.Id, Name = value.Name, Description = value.Description, ParentHolonId = value.ParentHolonId,
        UserId = value.UserId, ProviderName = value.ProviderName, ChainId = value.ChainId,
        AssetType = value.AssetType, TokenId = value.TokenId, Metadata = new(value.Metadata),
        PeerHolonIds = [.. value.PeerHolonIds], CreatedDate = value.CreatedDate, ModifiedDate = value.ModifiedDate,
        IsActive = value.IsActive, SourceHolonId = value.SourceHolonId, OriginUserId = value.OriginUserId, IsPublic = value.IsPublic
    };

    public static Wallet Copy(IWallet value) => new()
    {
        Id = value.Id, UserId = value.UserId, ChainType = value.ChainType, Address = value.Address,
        PublicKey = value.PublicKey, Label = value.Label, IsDefault = value.IsDefault, WalletType = value.WalletType,
        EncryptedPrivateKey = value.EncryptedPrivateKey, EncryptedSeedPhrase = value.EncryptedSeedPhrase,
        CreatedDate = value.CreatedDate
    };
}
