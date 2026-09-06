using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Users;

public class UserNFTService : IUserNFTService
{
    private readonly INftStore _nftStore;
    private readonly IHolonStore _holonStore;
    private readonly IWalletStore _walletStore;

    public UserNFTService(INftStore nftStore, IHolonStore holonStore, IWalletStore walletStore)
    {
        _nftStore = nftStore;
        _holonStore = holonStore;
        _walletStore = walletStore;
    }

    // ─── User NFT CRUD ───

    public async Task<AZOAResult<IUserNFT>> MintUserNFTAsync(Guid userId, UserNFTMintModel model, AZOARequest? request = null)
    {
        var userNFT = new UserNFT
        {
            UserId = userId,
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

        return await _nftStore.UpsertUserNFTAsync(userNFT, default);
    }

    public async Task<AZOAResult<IUserNFT>> GetUserNFTAsync(Guid id, AZOARequest? request = null)
    {
        return await _nftStore.GetUserNFTByIdAsync(id, default);
    }

    public async Task<AZOAResult<IUserNFT>> GetUserNFTByTokenIdAsync(string chainType, string nftContractAddress, string tokenId, AZOARequest? request = null)
    {
        return await _nftStore.GetUserNFTByTokenIdAsync(chainType, nftContractAddress, tokenId, default);
    }

    public async Task<AZOAResult<IEnumerable<IUserNFT>>> GetUserNFTsByUserAsync(Guid userId, AZOARequest? request = null)
    {
        return await _nftStore.GetUserNFTsByUserAsync(userId, default);
    }

    public async Task<AZOAResult<bool>> TransferUserNFTAsync(Guid id, string recipientAddress, Guid userId, AZOARequest? request = null)
    {
        var loadResult = await _nftStore.GetUserNFTByIdAsync(id, default);
        if (loadResult.IsError || loadResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = loadResult.Message ?? "User NFT not found." };

        var nft = loadResult.Result;
        if (nft.UserId != userId)
            return new AZOAResult<bool> { IsError = true, Message = "User NFT is owned by a different user." };

        if (!nft.IsTransferable)
            return new AZOAResult<bool> { IsError = true, Message = "This User NFT is not transferable." };

        if (nft.IsSoulbound)
            return new AZOAResult<bool> { IsError = true, Message = "Soulbound User NFTs cannot be transferred." };

        nft.CurrentOwner = recipientAddress;
        nft.LastTransferDate = DateTime.UtcNow;

        var saveResult = await _nftStore.UpsertUserNFTAsync(nft, default);
        if (saveResult.IsError)
            return new AZOAResult<bool> { IsError = true, Message = saveResult.Message };

        return new AZOAResult<bool> { Result = true, Message = "User NFT transferred." };
    }

    public async Task<AZOAResult<bool>> BurnUserNFTAsync(Guid id, Guid userId, AZOARequest? request = null)
    {
        var loadResult = await _nftStore.GetUserNFTByIdAsync(id, default);
        if (loadResult.IsError || loadResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = loadResult.Message ?? "User NFT not found." };

        if (loadResult.Result.UserId != userId)
            return new AZOAResult<bool> { IsError = true, Message = "User NFT is owned by a different user." };

        return await _nftStore.DeleteUserNFTAsync(id, default);
    }

    // ─── Holon NFT Binding ───

    public async Task<AZOAResult<IHolonNFTBinding>> BindHolonToUserNFTAsync(Guid holonId, Guid userNFTId, HolonNFTBindingModel model, Guid userId, AZOARequest? request = null)
    {
        var ownership = await VerifyNftOwnedByAsync(userNFTId, userId);
        if (ownership != null)
            return new AZOAResult<IHolonNFTBinding> { IsError = true, Message = ownership };

        // Bind target must also belong to the caller — otherwise a victim's holon can be bound to an attacker's NFT.
        var holonResult = await _holonStore.GetByIdAsync(holonId, default);
        if (holonResult.IsError || holonResult.Result == null)
            return new AZOAResult<IHolonNFTBinding> { IsError = true, Message = holonResult.Message ?? "Holon not found." };
        if (holonResult.Result.UserId != userId)
            return new AZOAResult<IHolonNFTBinding> { IsError = true, Message = "Target holon is owned by a different user." };

        var binding = new HolonNFTBinding
        {
            HolonId = holonId,
            UserNFTId = userNFTId,
            Role = model.Role,
            PermissionLevel = model.PermissionLevel,
            Permissions = model.Permissions,
            CreatedDate = DateTime.UtcNow,
            IsActive = true
        };

        return await _nftStore.UpsertHolonNFTBindingAsync(binding, default);
    }

    public async Task<AZOAResult<IEnumerable<IHolonNFTBinding>>> GetHolonBindingsAsync(Guid userNFTId, AZOARequest? request = null)
    {
        return await _nftStore.GetHolonNFTBindingsByUserNFTAsync(userNFTId, default);
    }

    public async Task<AZOAResult<bool>> UpdateHolonBindingAsync(Guid id, HolonNFTBindingUpdateModel model, Guid userId, AZOARequest? request = null)
    {
        var loadResult = await _nftStore.GetHolonNFTBindingByIdAsync(id, default);
        if (loadResult.IsError || loadResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = loadResult.Message ?? "Holon binding not found." };

        var binding = loadResult.Result;
        var ownership = await VerifyNftOwnedByAsync(binding.UserNFTId, userId);
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

    public async Task<AZOAResult<bool>> RemoveHolonBindingAsync(Guid id, Guid userId, AZOARequest? request = null)
    {
        var loadResult = await _nftStore.GetHolonNFTBindingByIdAsync(id, default);
        if (loadResult.IsError || loadResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = loadResult.Message ?? "Holon binding not found." };

        var ownership = await VerifyNftOwnedByAsync(loadResult.Result.UserNFTId, userId);
        if (ownership != null)
            return new AZOAResult<bool> { IsError = true, Message = ownership };

        return await _nftStore.DeleteHolonNFTBindingAsync(id, default);
    }

    // ─── Wallet NFT Binding ───

    public async Task<AZOAResult<IWalletNFTBinding>> BindWalletToUserNFTAsync(Guid walletId, Guid userNFTId, WalletNFTBindingModel model, Guid userId, AZOARequest? request = null)
    {
        var ownership = await VerifyNftOwnedByAsync(userNFTId, userId);
        if (ownership != null)
            return new AZOAResult<IWalletNFTBinding> { IsError = true, Message = ownership };

        // Bind target must also belong to the caller — otherwise a victim's wallet can be bound to an attacker's NFT.
        var walletResult = await _walletStore.GetByIdAsync(walletId, default);
        if (walletResult.IsError || walletResult.Result == null)
            return new AZOAResult<IWalletNFTBinding> { IsError = true, Message = walletResult.Message ?? "Wallet not found." };
        if (walletResult.Result.UserId != userId)
            return new AZOAResult<IWalletNFTBinding> { IsError = true, Message = "Target wallet is owned by a different user." };

        var binding = new WalletNFTBinding
        {
            WalletId = walletId,
            UserNFTId = userNFTId,
            BindingType = model.BindingType,
            AccessLevel = model.AccessLevel,
            AccessPermissions = model.AccessPermissions,
            CreatedDate = DateTime.UtcNow,
            IsActive = true
        };

        return await _nftStore.UpsertWalletNFTBindingAsync(binding, default);
    }

    public async Task<AZOAResult<IEnumerable<IWalletNFTBinding>>> GetWalletBindingsAsync(Guid userNFTId, AZOARequest? request = null)
    {
        return await _nftStore.GetWalletNFTBindingsByUserNFTAsync(userNFTId, default);
    }

    public async Task<AZOAResult<bool>> UpdateWalletBindingAsync(Guid id, WalletNFTBindingUpdateModel model, Guid userId, AZOARequest? request = null)
    {
        var loadResult = await _nftStore.GetWalletNFTBindingByIdAsync(id, default);
        if (loadResult.IsError || loadResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = loadResult.Message ?? "Wallet binding not found." };

        var binding = loadResult.Result;
        var ownership = await VerifyNftOwnedByAsync(binding.UserNFTId, userId);
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

    public async Task<AZOAResult<bool>> RemoveWalletBindingAsync(Guid id, Guid userId, AZOARequest? request = null)
    {
        var loadResult = await _nftStore.GetWalletNFTBindingByIdAsync(id, default);
        if (loadResult.IsError || loadResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = loadResult.Message ?? "Wallet binding not found." };

        var ownership = await VerifyNftOwnedByAsync(loadResult.Result.UserNFTId, userId);
        if (ownership != null)
            return new AZOAResult<bool> { IsError = true, Message = ownership };

        return await _nftStore.DeleteWalletNFTBindingAsync(id, default);
    }

    // ─── Composite Operations ───

    public async Task<AZOAResult<UserNFTCompositeResult>> GetUserNFTCompositeAsync(Guid userNFTId, AZOARequest? request = null)
    {
        var nftResult = await _nftStore.GetUserNFTByIdAsync(userNFTId, default);
        if (nftResult.IsError || nftResult.Result == null)
            return new AZOAResult<UserNFTCompositeResult> { IsError = true, Message = nftResult.Message ?? "User NFT not found." };

        var holonBindingsResult = await _nftStore.GetHolonNFTBindingsByUserNFTAsync(userNFTId, default);
        var walletBindingsResult = await _nftStore.GetWalletNFTBindingsByUserNFTAsync(userNFTId, default);

        var composite = BuildComposite(
            nftResult.Result,
            holonBindingsResult.Result,
            walletBindingsResult.Result);

        return new AZOAResult<UserNFTCompositeResult> { Result = composite, Message = "Success" };
    }

    public async Task<AZOAResult<IEnumerable<UserNFTCompositeResult>>> GetUserNFTCompositesByUserAsync(Guid userId, AZOARequest? request = null)
    {
        var nftsResult = await _nftStore.GetUserNFTsByUserAsync(userId, default);
        if (nftsResult.IsError || nftsResult.Result == null)
            return new AZOAResult<IEnumerable<UserNFTCompositeResult>> { IsError = true, Message = nftsResult.Message };

        var composites = new List<UserNFTCompositeResult>();
        foreach (var nft in nftsResult.Result)
        {
            var holonBindings = await _nftStore.GetHolonNFTBindingsByUserNFTAsync(nft.Id, default);
            var walletBindings = await _nftStore.GetWalletNFTBindingsByUserNFTAsync(nft.Id, default);
            composites.Add(BuildComposite(nft, holonBindings.Result, walletBindings.Result));
        }

        return new AZOAResult<IEnumerable<UserNFTCompositeResult>> { Result = composites, Message = "Success" };
    }

    // ─── Verification ───

    public async Task<AZOAResult<bool>> VerifyUserNFTOwnershipAsync(Guid userId, string chainType, string nftContractAddress, string tokenId, AZOARequest? request = null)
    {
        var nftResult = await _nftStore.GetUserNFTByTokenIdAsync(chainType, nftContractAddress, tokenId, default);
        if (nftResult.IsError || nftResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = nftResult.Message ?? "User NFT not found." };

        bool isOwner = nftResult.Result.UserId == userId;
        return new AZOAResult<bool> { Result = isOwner, Message = isOwner ? "Ownership verified." : "User does not own this NFT." };
    }

    public async Task<AZOAResult<bool>> VerifyHolonAccessAsync(Guid userNFTId, Guid holonId, string requiredPermission, AZOARequest? request = null)
    {
        var bindingsResult = await _nftStore.GetHolonNFTBindingsByUserNFTAsync(userNFTId, default);
        if (bindingsResult.IsError || bindingsResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = bindingsResult.Message ?? "No holon bindings found." };

        var binding = bindingsResult.Result.FirstOrDefault(b => b.HolonId == holonId && b.IsActive);
        if (binding == null)
            return new AZOAResult<bool> { Result = false, Message = "No active binding found for the specified holon." };

        bool hasAccess = string.Equals(binding.PermissionLevel, "full", StringComparison.OrdinalIgnoreCase)
            || binding.Permissions.ContainsKey(requiredPermission);

        return new AZOAResult<bool> { Result = hasAccess, Message = hasAccess ? "Access verified." : "Insufficient permissions." };
    }

    public async Task<AZOAResult<bool>> VerifyWalletAccessAsync(Guid userNFTId, Guid walletId, string requiredAccess, AZOARequest? request = null)
    {
        var bindingsResult = await _nftStore.GetWalletNFTBindingsByUserNFTAsync(userNFTId, default);
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
    /// UserNFT's UserId. Returns null when the NFT is owned by
    /// <paramref name="userId"/>, otherwise an error message to surface.
    /// </summary>
    private async Task<string?> VerifyNftOwnedByAsync(Guid userNFTId, Guid userId)
    {
        var nftResult = await _nftStore.GetUserNFTByIdAsync(userNFTId, default);
        if (nftResult.IsError || nftResult.Result == null)
            return nftResult.Message ?? "User NFT not found.";
        if (nftResult.Result.UserId != userId)
            return "User NFT is owned by a different user.";
        return null;
    }

    private static UserNFTCompositeResult BuildComposite(
        IUserNFT nft,
        IEnumerable<IHolonNFTBinding>? holonBindings,
        IEnumerable<IWalletNFTBinding>? walletBindings)
    {
        return new UserNFTCompositeResult
        {
            UserNFTId = nft.Id,
            UserId = nft.UserId,
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
