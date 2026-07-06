using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Avatar;

public class AvatarNFTService : IAvatarNFTService
{
    private readonly INftStore _nftStore;

    public AvatarNFTService(INftStore nftStore)
    {
        _nftStore = nftStore;
    }

    // ─── Avatar NFT CRUD ───

    public async Task<AZOAResult<IAvatarNFT>> MintAvatarNFTAsync(Guid avatarId, AvatarNFTMintModel model, AZOARequest? request = null)
    {
        var avatarNFT = new AvatarNFT
        {
            AvatarId = avatarId,
            ChainType = model.ChainType,
            // Chain-mint semantics: the id is assigned at mint when the caller
            // supplies none. Required non-empty by the nft_ownership schema.
            TokenId = string.IsNullOrWhiteSpace(model.TokenId)
                ? Guid.NewGuid().ToString("N")
                : model.TokenId,
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

        return await _nftStore.UpsertAvatarNFTAsync(avatarNFT, default);
    }

    public async Task<AZOAResult<IAvatarNFT>> GetAvatarNFTAsync(Guid id, AZOARequest? request = null)
    {
        return await _nftStore.GetAvatarNFTByIdAsync(id, default);
    }

    public async Task<AZOAResult<IAvatarNFT>> GetAvatarNFTByTokenIdAsync(string chainType, string nftContractAddress, string tokenId, AZOARequest? request = null)
    {
        return await _nftStore.GetAvatarNFTByTokenIdAsync(chainType, nftContractAddress, tokenId, default);
    }

    public async Task<AZOAResult<IEnumerable<IAvatarNFT>>> GetAvatarNFTsByAvatarAsync(Guid avatarId, AZOARequest? request = null)
    {
        return await _nftStore.GetAvatarNFTsByAvatarAsync(avatarId, default);
    }

    public async Task<AZOAResult<bool>> TransferAvatarNFTAsync(Guid id, string recipientAddress, Guid avatarId, AZOARequest? request = null)
    {
        var loadResult = await _nftStore.GetAvatarNFTByIdAsync(id, default);
        if (loadResult.IsError || loadResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = loadResult.Message ?? "Avatar NFT not found." };

        var nft = loadResult.Result;
        if (nft.AvatarId != avatarId)
            return new AZOAResult<bool> { IsError = true, Message = "Avatar NFT is owned by a different avatar." };

        if (!nft.IsTransferable)
            return new AZOAResult<bool> { IsError = true, Message = "This Avatar NFT is not transferable." };

        if (nft.IsSoulbound)
            return new AZOAResult<bool> { IsError = true, Message = "Soulbound Avatar NFTs cannot be transferred." };

        nft.CurrentOwner = recipientAddress;
        nft.LastTransferDate = DateTime.UtcNow;

        var saveResult = await _nftStore.UpsertAvatarNFTAsync(nft, default);
        if (saveResult.IsError)
            return new AZOAResult<bool> { IsError = true, Message = saveResult.Message };

        return new AZOAResult<bool> { Result = true, Message = "Avatar NFT transferred." };
    }

    public async Task<AZOAResult<bool>> BurnAvatarNFTAsync(Guid id, Guid avatarId, AZOARequest? request = null)
    {
        var loadResult = await _nftStore.GetAvatarNFTByIdAsync(id, default);
        if (loadResult.IsError || loadResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = loadResult.Message ?? "Avatar NFT not found." };

        if (loadResult.Result.AvatarId != avatarId)
            return new AZOAResult<bool> { IsError = true, Message = "Avatar NFT is owned by a different avatar." };

        return await _nftStore.DeleteAvatarNFTAsync(id, default);
    }

    // ─── Holon NFT Binding ───

    public async Task<AZOAResult<IHolonNFTBinding>> BindHolonToAvatarNFTAsync(Guid holonId, Guid avatarNFTId, HolonNFTBindingModel model, Guid avatarId, AZOARequest? request = null)
    {
        var ownership = await VerifyNftOwnedByAsync(avatarNFTId, avatarId);
        if (ownership != null)
            return new AZOAResult<IHolonNFTBinding> { IsError = true, Message = ownership };

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

        return await _nftStore.UpsertHolonNFTBindingAsync(binding, default);
    }

    public async Task<AZOAResult<IEnumerable<IHolonNFTBinding>>> GetHolonBindingsAsync(Guid avatarNFTId, AZOARequest? request = null)
    {
        return await _nftStore.GetHolonNFTBindingsByAvatarNFTAsync(avatarNFTId, default);
    }

    public async Task<AZOAResult<bool>> UpdateHolonBindingAsync(Guid id, HolonNFTBindingUpdateModel model, Guid avatarId, AZOARequest? request = null)
    {
        var loadResult = await _nftStore.GetHolonNFTBindingByIdAsync(id, default);
        if (loadResult.IsError || loadResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = loadResult.Message ?? "Holon binding not found." };

        var binding = loadResult.Result;
        var ownership = await VerifyNftOwnedByAsync(binding.AvatarNFTId, avatarId);
        if (ownership != null)
            return new AZOAResult<bool> { IsError = true, Message = ownership };

        if (model.Role != null) binding.Role = model.Role;
        if (model.PermissionLevel != null) binding.PermissionLevel = model.PermissionLevel;
        if (model.Permissions != null) binding.Permissions = model.Permissions;
        if (model.IsActive.HasValue) binding.IsActive = model.IsActive.Value;
        binding.LastUpdatedDate = DateTime.UtcNow;

        var saveResult = await _nftStore.UpsertHolonNFTBindingAsync(binding, default);
        if (saveResult.IsError)
            return new AZOAResult<bool> { IsError = true, Message = saveResult.Message };

        return new AZOAResult<bool> { Result = true, Message = "Holon binding updated." };
    }

    public async Task<AZOAResult<bool>> RemoveHolonBindingAsync(Guid id, Guid avatarId, AZOARequest? request = null)
    {
        var loadResult = await _nftStore.GetHolonNFTBindingByIdAsync(id, default);
        if (loadResult.IsError || loadResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = loadResult.Message ?? "Holon binding not found." };

        var ownership = await VerifyNftOwnedByAsync(loadResult.Result.AvatarNFTId, avatarId);
        if (ownership != null)
            return new AZOAResult<bool> { IsError = true, Message = ownership };

        return await _nftStore.DeleteHolonNFTBindingAsync(id, default);
    }

    // ─── Wallet NFT Binding ───

    public async Task<AZOAResult<IWalletNFTBinding>> BindWalletToAvatarNFTAsync(Guid walletId, Guid avatarNFTId, WalletNFTBindingModel model, Guid avatarId, AZOARequest? request = null)
    {
        var ownership = await VerifyNftOwnedByAsync(avatarNFTId, avatarId);
        if (ownership != null)
            return new AZOAResult<IWalletNFTBinding> { IsError = true, Message = ownership };

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

        return await _nftStore.UpsertWalletNFTBindingAsync(binding, default);
    }

    public async Task<AZOAResult<IEnumerable<IWalletNFTBinding>>> GetWalletBindingsAsync(Guid avatarNFTId, AZOARequest? request = null)
    {
        return await _nftStore.GetWalletNFTBindingsByAvatarNFTAsync(avatarNFTId, default);
    }

    public async Task<AZOAResult<bool>> UpdateWalletBindingAsync(Guid id, WalletNFTBindingUpdateModel model, Guid avatarId, AZOARequest? request = null)
    {
        var loadResult = await _nftStore.GetWalletNFTBindingByIdAsync(id, default);
        if (loadResult.IsError || loadResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = loadResult.Message ?? "Wallet binding not found." };

        var binding = loadResult.Result;
        var ownership = await VerifyNftOwnedByAsync(binding.AvatarNFTId, avatarId);
        if (ownership != null)
            return new AZOAResult<bool> { IsError = true, Message = ownership };

        if (model.BindingType != null) binding.BindingType = model.BindingType;
        if (model.AccessLevel != null) binding.AccessLevel = model.AccessLevel;
        if (model.AccessPermissions != null) binding.AccessPermissions = model.AccessPermissions;
        if (model.IsActive.HasValue) binding.IsActive = model.IsActive.Value;
        binding.LastUpdatedDate = DateTime.UtcNow;

        var saveResult = await _nftStore.UpsertWalletNFTBindingAsync(binding, default);
        if (saveResult.IsError)
            return new AZOAResult<bool> { IsError = true, Message = saveResult.Message };

        return new AZOAResult<bool> { Result = true, Message = "Wallet binding updated." };
    }

    public async Task<AZOAResult<bool>> RemoveWalletBindingAsync(Guid id, Guid avatarId, AZOARequest? request = null)
    {
        var loadResult = await _nftStore.GetWalletNFTBindingByIdAsync(id, default);
        if (loadResult.IsError || loadResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = loadResult.Message ?? "Wallet binding not found." };

        var ownership = await VerifyNftOwnedByAsync(loadResult.Result.AvatarNFTId, avatarId);
        if (ownership != null)
            return new AZOAResult<bool> { IsError = true, Message = ownership };

        return await _nftStore.DeleteWalletNFTBindingAsync(id, default);
    }

    // ─── Composite Operations ───

    public async Task<AZOAResult<AvatarNFTCompositeResult>> GetAvatarNFTCompositeAsync(Guid avatarNFTId, AZOARequest? request = null)
    {
        var nftResult = await _nftStore.GetAvatarNFTByIdAsync(avatarNFTId, default);
        if (nftResult.IsError || nftResult.Result == null)
            return new AZOAResult<AvatarNFTCompositeResult> { IsError = true, Message = nftResult.Message ?? "Avatar NFT not found." };

        var holonBindingsResult = await _nftStore.GetHolonNFTBindingsByAvatarNFTAsync(avatarNFTId, default);
        var walletBindingsResult = await _nftStore.GetWalletNFTBindingsByAvatarNFTAsync(avatarNFTId, default);

        var composite = BuildComposite(
            nftResult.Result,
            holonBindingsResult.Result,
            walletBindingsResult.Result);

        return new AZOAResult<AvatarNFTCompositeResult> { Result = composite, Message = "Success" };
    }

    public async Task<AZOAResult<IEnumerable<AvatarNFTCompositeResult>>> GetAvatarNFTCompositesByAvatarAsync(Guid avatarId, AZOARequest? request = null)
    {
        var nftsResult = await _nftStore.GetAvatarNFTsByAvatarAsync(avatarId, default);
        if (nftsResult.IsError || nftsResult.Result == null)
            return new AZOAResult<IEnumerable<AvatarNFTCompositeResult>> { IsError = true, Message = nftsResult.Message };

        var composites = new List<AvatarNFTCompositeResult>();
        foreach (var nft in nftsResult.Result)
        {
            var holonBindings = await _nftStore.GetHolonNFTBindingsByAvatarNFTAsync(nft.Id, default);
            var walletBindings = await _nftStore.GetWalletNFTBindingsByAvatarNFTAsync(nft.Id, default);
            composites.Add(BuildComposite(nft, holonBindings.Result, walletBindings.Result));
        }

        return new AZOAResult<IEnumerable<AvatarNFTCompositeResult>> { Result = composites, Message = "Success" };
    }

    // ─── Verification ───

    public async Task<AZOAResult<bool>> VerifyAvatarNFTOwnershipAsync(Guid avatarId, string chainType, string nftContractAddress, string tokenId, AZOARequest? request = null)
    {
        var nftResult = await _nftStore.GetAvatarNFTByTokenIdAsync(chainType, nftContractAddress, tokenId, default);
        if (nftResult.IsError || nftResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = nftResult.Message ?? "Avatar NFT not found." };

        bool isOwner = nftResult.Result.AvatarId == avatarId;
        return new AZOAResult<bool> { Result = isOwner, Message = isOwner ? "Ownership verified." : "Avatar does not own this NFT." };
    }

    public async Task<AZOAResult<bool>> VerifyHolonAccessAsync(Guid avatarNFTId, Guid holonId, string requiredPermission, AZOARequest? request = null)
    {
        var bindingsResult = await _nftStore.GetHolonNFTBindingsByAvatarNFTAsync(avatarNFTId, default);
        if (bindingsResult.IsError || bindingsResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = bindingsResult.Message ?? "No holon bindings found." };

        var binding = bindingsResult.Result.FirstOrDefault(b => b.HolonId == holonId && b.IsActive);
        if (binding == null)
            return new AZOAResult<bool> { Result = false, Message = "No active binding found for the specified holon." };

        bool hasAccess = string.Equals(binding.PermissionLevel, "full", StringComparison.OrdinalIgnoreCase)
            || binding.Permissions.ContainsKey(requiredPermission);

        return new AZOAResult<bool> { Result = hasAccess, Message = hasAccess ? "Access verified." : "Insufficient permissions." };
    }

    public async Task<AZOAResult<bool>> VerifyWalletAccessAsync(Guid avatarNFTId, Guid walletId, string requiredAccess, AZOARequest? request = null)
    {
        var bindingsResult = await _nftStore.GetWalletNFTBindingsByAvatarNFTAsync(avatarNFTId, default);
        if (bindingsResult.IsError || bindingsResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = bindingsResult.Message ?? "No wallet bindings found." };

        var binding = bindingsResult.Result.FirstOrDefault(b => b.WalletId == walletId && b.IsActive);
        if (binding == null)
            return new AZOAResult<bool> { Result = false, Message = "No active binding found for the specified wallet." };

        bool hasAccess = string.Equals(binding.AccessLevel, "full", StringComparison.OrdinalIgnoreCase)
            || binding.AccessPermissions.ContainsKey(requiredAccess);

        return new AZOAResult<bool> { Result = hasAccess, Message = hasAccess ? "Access verified." : "Insufficient access." };
    }

    // ─── Helpers ───

    /// <summary>
    /// Bindings carry no owner of their own — ownership is the parent
    /// AvatarNFT's AvatarId. Returns null when the NFT is owned by
    /// <paramref name="avatarId"/>, otherwise an error message to surface.
    /// </summary>
    private async Task<string?> VerifyNftOwnedByAsync(Guid avatarNFTId, Guid avatarId)
    {
        var nftResult = await _nftStore.GetAvatarNFTByIdAsync(avatarNFTId, default);
        if (nftResult.IsError || nftResult.Result == null)
            return nftResult.Message ?? "Avatar NFT not found.";
        if (nftResult.Result.AvatarId != avatarId)
            return "Avatar NFT is owned by a different avatar.";
        return null;
    }

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
