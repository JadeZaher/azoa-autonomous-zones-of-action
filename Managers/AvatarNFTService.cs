using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Managers;

public class AvatarNFTService : IAvatarNFTService
{
    private readonly ProviderContext _providerContext;

    public AvatarNFTService(ProviderContext providerContext)
    {
        _providerContext = providerContext;
    }

    private IOASISStorageProviderNFTExtensions NftProvider =>
        (IOASISStorageProviderNFTExtensions)_providerContext.CurrentProvider;

    // ─── Avatar NFT CRUD ───

    public async Task<OASISResult<IAvatarNFT>> MintAvatarNFTAsync(Guid avatarId, AvatarNFTMintModel model, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IAvatarNFT> { IsError = true, Message = activation.Message };

        var avatarNFT = new AvatarNFT
        {
            AvatarId = avatarId,
            ChainType = model.ChainType,
            NFTContractAddress = model.NFTContractAddress,
            TokenStandard = model.TokenStandard,
            MetadataURI = model.MetadataURI,
            ImageURI = model.ImageURI,
            Name = model.Name,
            Description = model.Description,
            Attributes = model.Attributes,
            RoyaltyPercentage = model.RoyaltyPercentage,
            RoyaltyRecipient = model.RoyaltyRecipient,
            IsSoulbound = model.IsSoulbound,
            IsTransferable = model.IsTransferable,
            MintedDate = DateTime.UtcNow,
            IsActive = true
        };

        return await NftProvider.SaveAvatarNFTAsync(avatarNFT);
    }

    public async Task<OASISResult<IAvatarNFT>> GetAvatarNFTAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IAvatarNFT> { IsError = true, Message = activation.Message };

        return await NftProvider.LoadAvatarNFTAsync(id);
    }

    public async Task<OASISResult<IAvatarNFT>> GetAvatarNFTByTokenIdAsync(string chainType, string nftContractAddress, string tokenId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IAvatarNFT> { IsError = true, Message = activation.Message };

        return await NftProvider.LoadAvatarNFTByTokenIdAsync(chainType, nftContractAddress, tokenId);
    }

    public async Task<OASISResult<IEnumerable<IAvatarNFT>>> GetAvatarNFTsByAvatarAsync(Guid avatarId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<IAvatarNFT>> { IsError = true, Message = activation.Message };

        return await NftProvider.LoadAvatarNFTsByAvatarAsync(avatarId);
    }

    public async Task<OASISResult<bool>> TransferAvatarNFTAsync(Guid id, string recipientAddress, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        var loadResult = await NftProvider.LoadAvatarNFTAsync(id);
        if (loadResult.IsError || loadResult.Result == null)
            return new OASISResult<bool> { IsError = true, Message = loadResult.Message ?? "Avatar NFT not found." };

        var nft = loadResult.Result;
        if (!nft.IsTransferable)
            return new OASISResult<bool> { IsError = true, Message = "This Avatar NFT is not transferable." };

        if (nft.IsSoulbound)
            return new OASISResult<bool> { IsError = true, Message = "Soulbound Avatar NFTs cannot be transferred." };

        nft.CurrentOwner = recipientAddress;
        nft.LastTransferDate = DateTime.UtcNow;

        var saveResult = await NftProvider.SaveAvatarNFTAsync(nft);
        if (saveResult.IsError)
            return new OASISResult<bool> { IsError = true, Message = saveResult.Message };

        return new OASISResult<bool> { Result = true, Message = "Avatar NFT transferred." };
    }

    public async Task<OASISResult<bool>> BurnAvatarNFTAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        return await NftProvider.DeleteAvatarNFTAsync(id);
    }

    // ─── Holon NFT Binding ───

    public async Task<OASISResult<IHolonNFTBinding>> BindHolonToAvatarNFTAsync(Guid holonId, Guid avatarNFTId, HolonNFTBindingModel model, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IHolonNFTBinding> { IsError = true, Message = activation.Message };

        var binding = new HolonNFTBinding
        {
            HolonId = holonId,
            AvatarNFTId = avatarNFTId,
            Role = model.Role,
            PermissionLevel = model.PermissionLevel,
            Permissions = model.Permissions,
            CreatedDate = DateTime.UtcNow,
            IsActive = true
        };

        return await NftProvider.SaveHolonNFTBindingAsync(binding);
    }

    public async Task<OASISResult<IEnumerable<IHolonNFTBinding>>> GetHolonBindingsAsync(Guid avatarNFTId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<IHolonNFTBinding>> { IsError = true, Message = activation.Message };

        return await NftProvider.LoadHolonNFTBindingsByAvatarNFTAsync(avatarNFTId);
    }

    public async Task<OASISResult<bool>> UpdateHolonBindingAsync(Guid id, HolonNFTBindingUpdateModel model, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        var loadResult = await NftProvider.LoadHolonNFTBindingAsync(id);
        if (loadResult.IsError || loadResult.Result == null)
            return new OASISResult<bool> { IsError = true, Message = loadResult.Message ?? "Holon binding not found." };

        var binding = loadResult.Result;
        if (model.Role != null) binding.Role = model.Role;
        if (model.PermissionLevel != null) binding.PermissionLevel = model.PermissionLevel;
        if (model.Permissions != null) binding.Permissions = model.Permissions;
        if (model.IsActive.HasValue) binding.IsActive = model.IsActive.Value;
        binding.LastUpdatedDate = DateTime.UtcNow;

        var saveResult = await NftProvider.SaveHolonNFTBindingAsync(binding);
        if (saveResult.IsError)
            return new OASISResult<bool> { IsError = true, Message = saveResult.Message };

        return new OASISResult<bool> { Result = true, Message = "Holon binding updated." };
    }

    public async Task<OASISResult<bool>> RemoveHolonBindingAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        return await NftProvider.DeleteHolonNFTBindingAsync(id);
    }

    // ─── Wallet NFT Binding ───

    public async Task<OASISResult<IWalletNFTBinding>> BindWalletToAvatarNFTAsync(Guid walletId, Guid avatarNFTId, WalletNFTBindingModel model, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IWalletNFTBinding> { IsError = true, Message = activation.Message };

        var binding = new WalletNFTBinding
        {
            WalletId = walletId,
            AvatarNFTId = avatarNFTId,
            BindingType = model.BindingType,
            AccessLevel = model.AccessLevel,
            AccessPermissions = model.AccessPermissions,
            CreatedDate = DateTime.UtcNow,
            IsActive = true
        };

        return await NftProvider.SaveWalletNFTBindingAsync(binding);
    }

    public async Task<OASISResult<IEnumerable<IWalletNFTBinding>>> GetWalletBindingsAsync(Guid avatarNFTId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<IWalletNFTBinding>> { IsError = true, Message = activation.Message };

        return await NftProvider.LoadWalletNFTBindingsByAvatarNFTAsync(avatarNFTId);
    }

    public async Task<OASISResult<bool>> UpdateWalletBindingAsync(Guid id, WalletNFTBindingUpdateModel model, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        var loadResult = await NftProvider.LoadWalletNFTBindingAsync(id);
        if (loadResult.IsError || loadResult.Result == null)
            return new OASISResult<bool> { IsError = true, Message = loadResult.Message ?? "Wallet binding not found." };

        var binding = loadResult.Result;
        if (model.BindingType != null) binding.BindingType = model.BindingType;
        if (model.AccessLevel != null) binding.AccessLevel = model.AccessLevel;
        if (model.AccessPermissions != null) binding.AccessPermissions = model.AccessPermissions;
        if (model.IsActive.HasValue) binding.IsActive = model.IsActive.Value;
        binding.LastUpdatedDate = DateTime.UtcNow;

        var saveResult = await NftProvider.SaveWalletNFTBindingAsync(binding);
        if (saveResult.IsError)
            return new OASISResult<bool> { IsError = true, Message = saveResult.Message };

        return new OASISResult<bool> { Result = true, Message = "Wallet binding updated." };
    }

    public async Task<OASISResult<bool>> RemoveWalletBindingAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        return await NftProvider.DeleteWalletNFTBindingAsync(id);
    }

    // ─── Composite Operations ───

    public async Task<OASISResult<AvatarNFTCompositeResult>> GetAvatarNFTCompositeAsync(Guid avatarNFTId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<AvatarNFTCompositeResult> { IsError = true, Message = activation.Message };

        var nftResult = await NftProvider.LoadAvatarNFTAsync(avatarNFTId);
        if (nftResult.IsError || nftResult.Result == null)
            return new OASISResult<AvatarNFTCompositeResult> { IsError = true, Message = nftResult.Message ?? "Avatar NFT not found." };

        var holonBindingsResult = await NftProvider.LoadHolonNFTBindingsByAvatarNFTAsync(avatarNFTId);
        var walletBindingsResult = await NftProvider.LoadWalletNFTBindingsByAvatarNFTAsync(avatarNFTId);

        var composite = BuildComposite(
            nftResult.Result,
            holonBindingsResult.Result,
            walletBindingsResult.Result);

        return new OASISResult<AvatarNFTCompositeResult> { Result = composite, Message = "Success" };
    }

    public async Task<OASISResult<IEnumerable<AvatarNFTCompositeResult>>> GetAvatarNFTCompositesByAvatarAsync(Guid avatarId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<AvatarNFTCompositeResult>> { IsError = true, Message = activation.Message };

        var nftsResult = await NftProvider.LoadAvatarNFTsByAvatarAsync(avatarId);
        if (nftsResult.IsError || nftsResult.Result == null)
            return new OASISResult<IEnumerable<AvatarNFTCompositeResult>> { IsError = true, Message = nftsResult.Message };

        var composites = new List<AvatarNFTCompositeResult>();
        foreach (var nft in nftsResult.Result)
        {
            var holonBindings = await NftProvider.LoadHolonNFTBindingsByAvatarNFTAsync(nft.Id);
            var walletBindings = await NftProvider.LoadWalletNFTBindingsByAvatarNFTAsync(nft.Id);
            composites.Add(BuildComposite(nft, holonBindings.Result, walletBindings.Result));
        }

        return new OASISResult<IEnumerable<AvatarNFTCompositeResult>> { Result = composites, Message = "Success" };
    }

    // ─── Verification ───

    public async Task<OASISResult<bool>> VerifyAvatarNFTOwnershipAsync(Guid avatarId, string chainType, string nftContractAddress, string tokenId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        var nftResult = await NftProvider.LoadAvatarNFTByTokenIdAsync(chainType, nftContractAddress, tokenId);
        if (nftResult.IsError || nftResult.Result == null)
            return new OASISResult<bool> { IsError = true, Message = nftResult.Message ?? "Avatar NFT not found." };

        bool isOwner = nftResult.Result.AvatarId == avatarId;
        return new OASISResult<bool> { Result = isOwner, Message = isOwner ? "Ownership verified." : "Avatar does not own this NFT." };
    }

    public async Task<OASISResult<bool>> VerifyHolonAccessAsync(Guid avatarNFTId, Guid holonId, string requiredPermission, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        var bindingsResult = await NftProvider.LoadHolonNFTBindingsByAvatarNFTAsync(avatarNFTId);
        if (bindingsResult.IsError || bindingsResult.Result == null)
            return new OASISResult<bool> { IsError = true, Message = bindingsResult.Message ?? "No holon bindings found." };

        var binding = bindingsResult.Result.FirstOrDefault(b => b.HolonId == holonId && b.IsActive);
        if (binding == null)
            return new OASISResult<bool> { Result = false, Message = "No active binding found for the specified holon." };

        bool hasAccess = string.Equals(binding.PermissionLevel, "full", StringComparison.OrdinalIgnoreCase)
            || binding.Permissions.ContainsKey(requiredPermission);

        return new OASISResult<bool> { Result = hasAccess, Message = hasAccess ? "Access verified." : "Insufficient permissions." };
    }

    public async Task<OASISResult<bool>> VerifyWalletAccessAsync(Guid avatarNFTId, Guid walletId, string requiredAccess, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        var bindingsResult = await NftProvider.LoadWalletNFTBindingsByAvatarNFTAsync(avatarNFTId);
        if (bindingsResult.IsError || bindingsResult.Result == null)
            return new OASISResult<bool> { IsError = true, Message = bindingsResult.Message ?? "No wallet bindings found." };

        var binding = bindingsResult.Result.FirstOrDefault(b => b.WalletId == walletId && b.IsActive);
        if (binding == null)
            return new OASISResult<bool> { Result = false, Message = "No active binding found for the specified wallet." };

        bool hasAccess = string.Equals(binding.AccessLevel, "full", StringComparison.OrdinalIgnoreCase)
            || binding.AccessPermissions.ContainsKey(requiredAccess);

        return new OASISResult<bool> { Result = hasAccess, Message = hasAccess ? "Access verified." : "Insufficient access." };
    }

    // ─── Helpers ───

    private static AvatarNFTCompositeResult BuildComposite(
        IAvatarNFT nft,
        IEnumerable<IHolonNFTBinding>? holonBindings,
        IEnumerable<IWalletNFTBinding>? walletBindings)
    {
        return new AvatarNFTCompositeResult
        {
            AvatarNFTId = nft.Id,
            AvatarId = nft.AvatarId,
            NFTContractAddress = nft.NFTContractAddress,
            TokenId = nft.TokenId,
            ChainType = nft.ChainType,
            Name = nft.Name ?? string.Empty,
            Description = nft.Description,
            ImageURI = nft.ImageURI,
            Attributes = nft.Attributes,
            CurrentOwner = nft.CurrentOwner,
            IsSoulbound = nft.IsSoulbound,
            IsTransferable = nft.IsTransferable,
            IsActive = nft.IsActive,
            MintedDate = nft.MintedDate,
            LastTransferDate = nft.LastTransferDate,
            HolonBindings = holonBindings?.Select(b => new HolonBindingInfo
            {
                HolonId = b.HolonId,
                HolonName = string.Empty, // Would need a holon lookup for the name
                Role = b.Role,
                PermissionLevel = b.PermissionLevel,
                Permissions = b.Permissions,
                IsActive = b.IsActive
            }).ToList() ?? new List<HolonBindingInfo>(),
            WalletBindings = walletBindings?.Select(b => new WalletBindingInfo
            {
                WalletId = b.WalletId,
                WalletAddress = string.Empty, // Would need a wallet lookup for the address
                ChainType = string.Empty,
                BindingType = b.BindingType,
                AccessLevel = b.AccessLevel,
                AccessPermissions = b.AccessPermissions,
                IsActive = b.IsActive
            }).ToList() ?? new List<WalletBindingInfo>()
        };
    }
}
