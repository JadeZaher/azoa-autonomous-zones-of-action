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
    private readonly IBlockchainOperationManager _blockchainOperationManager;

    public AvatarNFTService(ProviderContext providerContext, IBlockchainOperationManager blockchainOperationManager)
    {
        _providerContext = providerContext;
        _blockchainOperationManager = blockchainOperationManager;
    }

    public async Task<OASISResult<IAvatarNFT>> MintAvatarNFTAsync(Guid avatarId, AvatarNFTMintModel model, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IAvatarNFT> { IsError = true, Message = activation.Message };

        // Verify avatar exists
        var avatarResult = await _providerContext.CurrentProvider.LoadAvatarAsync(avatarId);
        if (avatarResult.IsError || avatarResult.Result == null)
            return new OASISResult<IAvatarNFT> { IsError = true, Message = "Avatar not found." };

        // Create AvatarNFT
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
            CurrentOwner = await GetAvatarWalletAddressAsync(avatarId, model.ChainType)
        };

        // Mint the NFT on blockchain
        var mintResult = await _blockchainOperationManager.MintNFTAsync(new Models.Requests.STARDappGenerationRequest
        {
            ChainType = model.ChainType,
            ContractAddress = model.NFTContractAddress,
            TokenId = Guid.NewGuid().ToString(),
            MetadataURI = model.MetadataURI,
            OwnerAddress = avatarNFT.CurrentOwner,
            RoyaltyPercentage = model.RoyaltyPercentage,
            RoyaltyRecipient = model.RoyaltyRecipient
        });

        if (mintResult.IsError)
            return new OASISResult<IAvatarNFT> { IsError = true, Message = mintResult.Message };

        // Save to database
        var saveResult = await _providerContext.CurrentProvider.SaveAvatarNFTAsync(avatarNFT);
        if (saveResult.IsError)
            return saveResult;

        // Create bindings if specified
        if (model.HolonBindings != null)
        {
            foreach (var binding in model.HolonBindings)
            {
                await BindHolonToAvatarNFTInternalAsync(
                    binding.Key, // holonId as string
                    saveResult.Result!.Id,
                    new HolonNFTBindingModel { Role = binding.Value, Permissions = new() { { "auto-created", "true" } } }
                );
            }
        }

        if (model.WalletBindings != null)
        {
            foreach (var binding in model.WalletBindings)
            {
                await BindWalletToAvatarNFTInternalAsync(
                    binding.Key, // walletId as string
                    saveResult.Result!.Id,
                    new WalletNFTBindingModel { BindingType = binding.Value, AccessPermissions = new() { { "auto-created", "true" } } }
                );
            }
        }

        return saveResult;
    }

    public async Task<OASISResult<IAvatarNFT>> GetAvatarNFTAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IAvatarNFT> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.LoadAvatarNFTAsync(id);
    }

    public async Task<OASISResult<IAvatarNFT>> GetAvatarNFTByTokenIdAsync(string chainType, string nftContractAddress, string tokenId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IAvatarNFT> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.LoadAvatarNFTByTokenIdAsync(chainType, nftContractAddress, tokenId);
    }

    public async Task<OASISResult<IEnumerable<IAvatarNFT>>> GetAvatarNFTsByAvatarAsync(Guid avatarId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<IAvatarNFT>> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.LoadAvatarNFTsByAvatarAsync(avatarId);
    }

    public async Task<OASISResult<bool>> TransferAvatarNFTAsync(Guid id, string recipientAddress, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        var nftResult = await _providerContext.CurrentProvider.LoadAvatarNFTAsync(id);
        if (nftResult.IsError || nftResult.Result == null)
            return new OASISResult<bool> { IsError = true, Message = "NFT not found." };

        var nft = nftResult.Result;
        if (nft.IsSoulbound)
            return new OASISResult<bool> { IsError = true, Message = "Cannot transfer soulbound NFT." };

        if (!nft.IsTransferable)
            return new OASISResult<bool> { IsError = true, Message = "NFT is not transferable." };

        // Transfer on blockchain
        var transferResult = await _blockchainOperationManager.TransferNFTAsync(new Models.Requests.STARDappGenerationRequest
        {
            ChainType = nft.ChainType,
            ContractAddress = nft.NFTContractAddress,
            TokenId = nft.TokenId,
            RecipientAddress = recipientAddress
        });

        if (transferResult.IsError)
            return new OASISResult<bool> { IsError = true, Message = transferResult.Message };

        // Update database
        nft.CurrentOwner = recipientAddress;
        nft.LastTransferDate = DateTime.UtcNow;
        nft.IsActive = false; // Mark original as inactive

        var updatedNFT = new AvatarNFT
        {
            Id = nft.Id,
            AvatarId = nft.AvatarId,
            ChainType = nft.ChainType,
            NFTContractAddress = nft.NFTContractAddress,
            TokenId = nft.TokenId,
            TokenStandard = nft.TokenStandard,
            MetadataURI = nft.MetadataURI,
            ImageURI = nft.ImageURI,
            Name = nft.Name,
            Description = nft.Description,
            Attributes = nft.Attributes,
            RoyaltyPercentage = nft.RoyaltyPercentage,
            RoyaltyRecipient = nft.RoyaltyRecipient,
            IsSoulbound = nft.IsSoulbound,
            IsTransferable = nft.IsTransferable,
            MintedDate = nft.MintedDate,
            LastTransferDate = nft.LastTransferDate,
            CurrentOwner = recipientAddress,
            IsActive = true
        };

        var saveResult = await _providerContext.CurrentProvider.SaveAvatarNFTAsync(updatedNFT);
        if (saveResult.IsError)
            return new OASISResult<bool> { IsError = true, Message = saveResult.Message };

        return new OASISResult<bool> { Result = true, Message = "NFT transferred successfully." };
    }

    public async Task<OASISResult<bool>> BurnAvatarNFTAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        var nftResult = await _providerContext.CurrentProvider.LoadAvatarNFTAsync(id);
        if (nftResult.IsError || nftResult.Result == null)
            return new OASISResult<bool> { IsError = true, Message = "NFT not found." };

        var nft = nftResult.Result;
        if (nft.IsSoulbound)
            return new OASISResult<bool> { IsError = true, Message = "Cannot burn soulbound NFT." };

        // Burn on blockchain
        var burnResult = await _blockchainOperationManager.BurnNFTAsync(new Models.Requests.STARDappGenerationRequest
        {
            ChainType = nft.ChainType,
            ContractAddress = nft.NFTContractAddress,
            TokenId = nft.TokenId
        });

        if (burnResult.IsError)
            return new OASISResult<bool> { IsError = true, Message = burnResult.Message };

        // Delete from database
        return await _providerContext.CurrentProvider.DeleteAvatarNFTAsync(id);
    }

    public async Task<OASISResult<IHolonNFTBinding>> BindHolonToAvatarNFTAsync(Guid holonId, Guid avatarNFTId, HolonNFTBindingModel model, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IHolonNFTBinding> { IsError = true, Message = activation.Message };

        return await BindHolonToAvatarNFTInternalAsync(holonId.ToString(), avatarNFTId, model);
    }

    public async Task<OASISResult<IEnumerable<IHolonNFTBinding>>> GetHolonBindingsAsync(Guid avatarNFTId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<IHolonNFTBinding>> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.LoadHolonNFTBindingsByAvatarNFTAsync(avatarNFTId);
    }

    public async Task<OASISResult<bool>> UpdateHolonBindingAsync(Guid id, HolonNFTBindingUpdateModel model, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        var bindingResult = await _providerContext.CurrentProvider.LoadHolonNFTBindingAsync(id);
        if (bindingResult.IsError || bindingResult.Result == null)
            return new OASISResult<bool> { IsError = true, Message = "Binding not found." };

        var binding = bindingResult.Result;
        if (model.Role != null) binding.Role = model.Role;
        if (model.PermissionLevel != null) binding.PermissionLevel = model.PermissionLevel;
        if (model.Permissions != null) binding.Permissions = model.Permissions;
        if (model.IsActive.HasValue) binding.IsActive = model.IsActive.Value;
        binding.LastUpdatedDate = DateTime.UtcNow;

        var saveResult = await _providerContext.CurrentProvider.SaveHolonNFTBindingAsync(binding);
        if (saveResult.IsError)
            return new OASISResult<bool> { IsError = true, Message = saveResult.Message };

        return new OASISResult<bool> { Result = true, Message = "Binding updated successfully." };
    }

    public async Task<OASISResult<bool>> RemoveHolonBindingAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.DeleteHolonNFTBindingAsync(id);
    }

    public async Task<OASISResult<IWalletNFTBinding>> BindWalletToAvatarNFTAsync(Guid walletId, Guid avatarNFTId, WalletNFTBindingModel model, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IWalletNFTBinding> { IsError = true, Message = activation.Message };

        return await BindWalletToAvatarNFTInternalAsync(walletId.ToString(), avatarNFTId, model);
    }

    public async Task<OASISResult<IEnumerable<IWalletNFTBinding>>> GetWalletBindingsAsync(Guid avatarNFTId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<IWalletNFTBinding>> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.LoadWalletNFTBindingsByAvatarNFTAsync(avatarNFTId);
    }

    public async Task<OASISResult<bool>> UpdateWalletBindingAsync(Guid id, WalletNFTBindingUpdateModel model, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        var bindingResult = await _providerContext.CurrentProvider.LoadWalletNFTBindingAsync(id);
        if (bindingResult.IsError || bindingResult.Result == null)
            return new OASISResult<bool> { IsError = true, Message = "Binding not found." };

        var binding = bindingResult.Result;
        if (model.BindingType != null) binding.BindingType = model.BindingType;
        if (model.AccessLevel != null) binding.AccessLevel = model.AccessLevel;
        if (model.AccessPermissions != null) binding.AccessPermissions = model.AccessPermissions;
        if (model.IsActive.HasValue) binding.IsActive = model.IsActive.Value;
        binding.LastUpdatedDate = DateTime.UtcNow;

        var saveResult = await _providerContext.CurrentProvider.SaveWalletNFTBindingAsync(binding);
        if (saveResult.IsError)
            return new OASISResult<bool> { IsError = true, Message = saveResult.Message };

        return new OASISResult<bool> { Result = true, Message = "Binding updated successfully." };
    }

    public async Task<OASISResult<bool>> RemoveWalletBindingAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.DeleteWalletNFTBindingAsync(id);
    }

    public async Task<OASISResult<AvatarNFTCompositeResult>> GetAvatarNFTCompositeAsync(Guid avatarNFTId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<AvatarNFTCompositeResult> { IsError = true, Message = activation.Message };

        var nftResult = await _providerContext.CurrentProvider.LoadAvatarNFTAsync(avatarNFTId);
        if (nftResult.IsError || nftResult.Result == null)
            return new OASISResult<AvatarNFTCompositeResult> { IsError = true, Message = "NFT not found." };

        var nft = nftResult.Result;
        var holonBindings = await _providerContext.CurrentProvider.LoadHolonNFTBindingsByAvatarNFTAsync(avatarNFTId);
        var walletBindings = await _providerContext.CurrentProvider.LoadWalletNFTBindingsByAvatarNFTAsync(avatarNFTId);

        var composite = new AvatarNFTCompositeResult
        {
            AvatarNFTId = nft.Id,
            AvatarId = nft.AvatarId,
            NFTContractAddress = nft.NFTContractAddress,
            TokenId = nft.TokenId,
            ChainType = nft.ChainType,
            Name = nft.Name ?? "",
            Description = nft.Description,
            ImageURI = nft.ImageURI,
            Attributes = nft.Attributes,
            CurrentOwner = nft.CurrentOwner,
            IsSoulbound = nft.IsSoulbound,
            IsTransferable = nft.IsTransferable,
            IsActive = nft.IsActive,
            MintedDate = nft.MintedDate,
            LastTransferDate = nft.LastTransferDate,
            HolonBindings = holonBindings.Result?.Select(b => new HolonBindingInfo
            {
                HolonId = b.HolonId,
                HolonName = "", // Would need to load holon to get name
                Role = b.Role,
                PermissionLevel = b.PermissionLevel,
                Permissions = b.Permissions,
                IsActive = b.IsActive
            }).ToList() ?? new List<HolonBindingInfo>(),
            WalletBindings = walletBindings.Result?.Select(b => new WalletBindingInfo
            {
                WalletId = b.WalletId,
                WalletAddress = "", // Would need to load wallet to get address
                ChainType = "", // Would need to load wallet to get chain type
                BindingType = b.BindingType,
                AccessLevel = b.AccessLevel,
                AccessPermissions = b.AccessPermissions,
                IsActive = b.IsActive
            }).ToList() ?? new List<WalletBindingInfo>()
        };

        return new OASISResult<AvatarNFTCompositeResult> { Result = composite, Message = "Composite data retrieved successfully." };
    }

    public async Task<OASISResult<IEnumerable<AvatarNFTCompositeResult>>> GetAvatarNFTCompositesByAvatarAsync(Guid avatarId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<AvatarNFTCompositeResult>> { IsError = true, Message = activation.Message };

        var nftsResult = await _providerContext.CurrentProvider.LoadAvatarNFTsByAvatarAsync(avatarId);
        if (nftsResult.IsError || nftsResult.Result == null)
            return new OASISResult<IEnumerable<AvatarNFTCompositeResult>> { IsError = true, Message = "No NFTs found for avatar." };

        var composites = new List<AvatarNFTCompositeResult>();
        foreach (var nft in nftsResult.Result)
        {
            var compositeResult = await GetAvatarNFTCompositeAsync(nft.Id);
            if (!compositeResult.IsError)
            {
                composites.Add(compositeResult.Result!);
            }
        }

        return new OASISResult<IEnumerable<AvatarNFTCompositeResult>> { Result = composites, Message = "Composite data retrieved successfully." };
    }

    public async Task<OASISResult<bool>> VerifyAvatarNFTOwnershipAsync(Guid avatarId, string chainType, string nftContractAddress, string tokenId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        var nftResult = await _providerContext.CurrentProvider.LoadAvatarNFTByTokenIdAsync(chainType, nftContractAddress, tokenId);
        if (nftResult.IsError || nftResult.Result == null)
            return new OASISResult<bool> { IsError = true, Message = "NFT not found." };

        var nft = nftResult.Result;
        var isOwner = nft.AvatarId == avatarId && nft.CurrentOwner == await GetAvatarWalletAddressAsync(avatarId, chainType);
        
        return new OASISResult<bool> { Result = isOwner, Message = isOwner ? "Ownership verified." : "Ownership verification failed." };
    }

    public async Task<OASISResult<bool>> VerifyHolonAccessAsync(Guid avatarNFTId, Guid holonId, string requiredPermission, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        var bindingResult = await _providerContext.CurrentProvider.LoadHolonNFTBindingsByAvatarNFTAsync(avatarNFTId);
        if (bindingResult.IsError || bindingResult.Result == null)
            return new OASISResult<bool> { IsError = true, Message = "No bindings found." };

        var binding = bindingResult.Result.FirstOrDefault(b => b.HolonId == holonId && b.IsActive);
        if (binding == null)
            return new OASISResult<bool> { IsError = true, Message = "No active binding found for holon." };

        var hasPermission = binding.Permissions.ContainsKey(requiredPermission) && 
                          bool.TryParse(binding.Permissions[requiredPermission], out bool permission) && permission;
        
        return new OASISResult<bool> { Result = hasPermission, Message = hasPermission ? "Access verified." : "Access denied." };
    }

    public async Task<OASISResult<bool>> VerifyWalletAccessAsync(Guid avatarNFTId, Guid walletId, string requiredAccess, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        var bindingResult = await _providerContext.CurrentProvider.LoadWalletNFTBindingsByAvatarNFTAsync(avatarNFTId);
        if (bindingResult.IsError || bindingResult.Result == null)
            return new OASISResult<bool> { IsError = true, Message = "No bindings found." };

        var binding = bindingResult.Result.FirstOrDefault(b => b.WalletId == walletId && b.IsActive);
        if (binding == null)
            return new OASISResult<bool> { IsError = true, Message = "No active binding found for wallet." };

        var hasAccess = binding.AccessPermissions.ContainsKey(requiredAccess) && 
                      bool.TryParse(binding.AccessPermissions[requiredAccess], out bool access) && access;
        
        return new OASISResult<bool> { Result = hasAccess, Message = hasAccess ? "Access verified." : "Access denied." };
    }

    private async Task<string> GetAvatarWalletAddressAsync(Guid avatarId, string chainType)
    {
        var walletsResult = await _providerContext.CurrentProvider.LoadWalletsByAvatarAsync(avatarId);
        if (walletsResult.IsError || walletsResult.Result == null)
            throw new InvalidOperationException("No wallets found for avatar.");

        var wallet = walletsResult.Result.FirstOrDefault(w => w.ChainType.Equals(chainType, StringComparison.OrdinalIgnoreCase));
        return wallet?.Address ?? throw new InvalidOperationException("No wallet found for specified chain type.");
    }

    private async Task<OASISResult<IHolonNFTBinding>> BindHolonToAvatarNFTInternalAsync(string holonId, Guid avatarNFTId, HolonNFTBindingModel model)
    {
        if (!Guid.TryParse(holonId, out Guid holonGuid))
            return new OASISResult<IHolonNFTBinding> { IsError = true, Message = "Invalid holon ID." };

        var binding = new HolonNFTBinding
        {
            HolonId = holonGuid,
            AvatarNFTId = avatarNFTId,
            Role = model.Role,
            PermissionLevel = model.PermissionLevel,
            Permissions = model.Permissions,
            CreatedDate = DateTime.UtcNow,
            IsActive = true
        };

        return await _providerContext.CurrentProvider.SaveHolonNFTBindingAsync(binding);
    }

    private async Task<OASISResult<IWalletNFTBinding>> BindWalletToAvatarNFTInternalAsync(string walletId, Guid avatarNFTId, WalletNFTBindingModel model)
    {
        if (!Guid.TryParse(walletId, out Guid walletGuid))
            return new OASISResult<IWalletNFTBinding> { IsError = true, Message = "Invalid wallet ID." };

        var binding = new WalletNFTBinding
        {
            WalletId = walletGuid,
            AvatarNFTId = avatarNFTId,
            BindingType = model.BindingType,
            AccessLevel = model.AccessLevel,
            AccessPermissions = model.AccessPermissions,
            CreatedDate = DateTime.UtcNow,
            IsActive = true
        };

        return await _providerContext.CurrentProvider.SaveWalletNFTBindingAsync(binding);
    }