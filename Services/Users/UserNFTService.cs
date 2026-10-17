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
    private readonly INftLifecycleService _lifecycle;

    public UserNFTService(INftStore nftStore, IHolonStore holonStore, IWalletStore walletStore, INftLifecycleService lifecycle)
    {
        _nftStore = nftStore;
        _holonStore = holonStore;
        _walletStore = walletStore;
        _lifecycle = lifecycle;
    }

    /// <inheritdoc/>
    public Task<AZOAResult<IUserNFT>> MintUserNFTAsync(Guid userId, UserNFTMintModel model, AZOARequest? request = null, Guid? actingTenantId = null)
        => MintConfirmedAsync(userId, userId, model, request, actingTenantId);

    /// <inheritdoc/>
    public Task<AZOAResult<IUserNFT>> MintUserNFTAsync(Guid issuerUserId, Guid recipientUserId, UserNFTMintModel model, AZOARequest? request = null, Guid? actingTenantId = null)
        => issuerUserId == recipientUserId
            ? Task.FromResult(AZOAResult<IUserNFT>.Failure("Use self-mint for a self-held badge."))
            : MintConfirmedAsync(issuerUserId, recipientUserId, model, request, actingTenantId);

    private async Task<AZOAResult<IUserNFT>> MintConfirmedAsync(Guid actor, Guid recipient, UserNFTMintModel model, AZOARequest? request, Guid? actingTenantId)
    {
        var result = await _lifecycle.MintAsync(actor, recipient, model, request?.IdempotencyKey ?? string.Empty, actingTenantId: actingTenantId ?? Guid.Empty, credential: request?.AuthenticationWitness);
        if (result.IsError || result.Result is null) return AZOAResult<IUserNFT>.Failure(result.Message);
        if (result.Result.State != "Completed")
            return AZOAResult<IUserNFT>.FailureWithCode($"NFT operation {result.Result.OperationId} is {result.Result.State}.", "NFT_PENDING");
        return await _nftStore.GetUserNFTByIdAsync(result.Result.NftId);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IUserNFT>> GetUserNFTAsync(Guid id, AZOARequest? request = null)
    {
        return await _nftStore.GetUserNFTByIdAsync(id, default);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IUserNFT>> GetUserNFTByTokenIdAsync(string chainType, string nftContractAddress, string tokenId, AZOARequest? request = null)
    {
        return await _nftStore.GetUserNFTByTokenIdAsync(chainType, nftContractAddress, tokenId, default);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<IUserNFT>>> GetUserNFTsByUserAsync(Guid userId, AZOARequest? request = null)
    {
        return await _nftStore.GetUserNFTsByUserAsync(userId, default);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> TransferUserNFTAsync(Guid id, string recipientAddress, Guid userId, AZOARequest? request = null, Guid? actingTenantId = null)
    {
        var result = await _lifecycle.MoveAsync(userId, id, recipientAddress, false, request?.IdempotencyKey ?? string.Empty, actingTenantId: actingTenantId ?? Guid.Empty, feeAuthorization: request?.NftFeeAuthorization);
        return Completed(result);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> BurnUserNFTAsync(Guid id, Guid userId, AZOARequest? request = null, Guid? actingTenantId = null)
    {
        var result = await _lifecycle.MoveAsync(userId, id, null, true, request?.IdempotencyKey ?? string.Empty, actingTenantId: actingTenantId ?? Guid.Empty, feeAuthorization: request?.NftFeeAuthorization);
        return Completed(result);
    }

    private static AZOAResult<bool> Completed(AZOAResult<NftLifecycleView> result)
        => result.IsError || result.Result is null ? AZOAResult<bool>.Failure(result.Message)
            : result.Result.State == "Completed" ? AZOAResult<bool>.Success(true)
            : AZOAResult<bool>.FailureWithCode($"NFT operation {result.Result.OperationId} is {result.Result.State}.", "NFT_PENDING");

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<IHolonNFTBinding>>> GetHolonBindingsAsync(Guid userNFTId, AZOARequest? request = null, Guid? callerUserId = null)
    {
        if (callerUserId is null || await VerifyNftOwnedByAsync(userNFTId, callerUserId.Value) is not null)
            return AZOAResult<IEnumerable<IHolonNFTBinding>>.Failure("Current NFT ownership is required.");
        return await _nftStore.GetHolonNFTBindingsByUserNFTAsync(userNFTId, default);
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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


    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<IWalletNFTBinding>>> GetWalletBindingsAsync(Guid userNFTId, AZOARequest? request = null, Guid? callerUserId = null)
    {
        if (callerUserId is null || await VerifyNftOwnedByAsync(userNFTId, callerUserId.Value) is not null)
            return AZOAResult<IEnumerable<IWalletNFTBinding>>.Failure("Current NFT ownership is required.");
        return await _nftStore.GetWalletNFTBindingsByUserNFTAsync(userNFTId, default);
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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


    /// <inheritdoc/>
    public async Task<AZOAResult<UserNFTCompositeResult>> GetUserNFTCompositeAsync(Guid userNFTId, AZOARequest? request = null, Guid? callerUserId = null)
    {
        if (callerUserId is null || await VerifyNftOwnedByAsync(userNFTId, callerUserId.Value) is not null)
            return AZOAResult<UserNFTCompositeResult>.Failure("Current NFT ownership is required.");
        var nftResult = await _nftStore.GetUserNFTByIdAsync(userNFTId, default);
        if (nftResult.IsError || nftResult.Result == null)
            return new AZOAResult<UserNFTCompositeResult> { IsError = true, Message = nftResult.Message ?? "User NFT not found." };

        var holonBindingsResult = await _nftStore.GetHolonNFTBindingsByUserNFTAsync(userNFTId, default);
        var walletBindingsResult = await _nftStore.GetWalletNFTBindingsByUserNFTAsync(userNFTId, default);

        var composite = await BuildCompositeAsync(
            nftResult.Result,
            holonBindingsResult.Result,
            walletBindingsResult.Result);

        return new AZOAResult<UserNFTCompositeResult> { Result = composite, Message = "Success" };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<UserNFTCompositeResult>>> GetUserNFTCompositesByUserAsync(Guid userId, AZOARequest? request = null, Guid? callerUserId = null)
    {
        if (callerUserId != userId) return AZOAResult<IEnumerable<UserNFTCompositeResult>>.Failure("Caller identity does not match requested user.");
        var nftsResult = await _nftStore.GetUserNFTsByUserAsync(userId, default);
        if (nftsResult.IsError || nftsResult.Result == null)
            return new AZOAResult<IEnumerable<UserNFTCompositeResult>> { IsError = true, Message = nftsResult.Message };

        var composites = new List<UserNFTCompositeResult>();
        foreach (var nft in nftsResult.Result)
        {
            var composite = await GetUserNFTCompositeAsync(nft.Id, request, callerUserId);
            if (!composite.IsError && composite.Result is not null) composites.Add(composite.Result);
        }

        return new AZOAResult<IEnumerable<UserNFTCompositeResult>> { Result = composites, Message = "Success" };
    }


    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> VerifyUserNFTOwnershipAsync(Guid userId, string chainType, string nftContractAddress, string tokenId, AZOARequest? request = null)
    {
        var nftResult = await _nftStore.GetUserNFTByTokenIdAsync(chainType, nftContractAddress, tokenId, default);
        if (nftResult.IsError || nftResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = nftResult.Message ?? "User NFT not found." };

        bool isOwner = nftResult.Result.IsActive && await _lifecycle.IsCurrentOwnerAsync(userId, nftResult.Result.Id);
        return new AZOAResult<bool> { Result = isOwner, Message = isOwner ? "Ownership verified." : "User does not own this NFT." };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> VerifyHolonAccessAsync(Guid userNFTId, Guid holonId, string requiredPermission, AZOARequest? request = null, Guid? callerUserId = null)
    {
        if (callerUserId is null || string.IsNullOrWhiteSpace(requiredPermission)
            || await VerifyNftOwnedByAsync(userNFTId, callerUserId.Value) is not null)
            return AZOAResult<bool>.Success(false);

        var bindingsResult = await _nftStore.GetHolonNFTBindingsByUserNFTAsync(userNFTId, default);
        if (bindingsResult.IsError || bindingsResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = bindingsResult.Message ?? "No holon bindings found." };

        var binding = bindingsResult.Result.FirstOrDefault(b => b.HolonId == holonId && b.IsActive);
        if (binding == null)
            return new AZOAResult<bool> { Result = false, Message = "No active binding found for the specified holon." };

        bool hasAccess = NftBindingPermissions.Grants(binding.PermissionLevel, binding.Permissions, requiredPermission);

        return new AZOAResult<bool> { Result = hasAccess, Message = hasAccess ? "Access verified." : "Insufficient permissions." };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> VerifyWalletAccessAsync(Guid userNFTId, Guid walletId, string requiredAccess, AZOARequest? request = null, Guid? callerUserId = null)
    {
        if (callerUserId is null || string.IsNullOrWhiteSpace(requiredAccess)
            || await VerifyNftOwnedByAsync(userNFTId, callerUserId.Value) is not null)
            return AZOAResult<bool>.Success(false);

        var bindingsResult = await _nftStore.GetWalletNFTBindingsByUserNFTAsync(userNFTId, default);
        if (bindingsResult.IsError || bindingsResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = bindingsResult.Message ?? "No wallet bindings found." };

        var binding = bindingsResult.Result.FirstOrDefault(b => b.WalletId == walletId && b.IsActive);
        if (binding == null)
            return new AZOAResult<bool> { Result = false, Message = "No active binding found for the specified wallet." };

        bool hasAccess = NftBindingPermissions.Grants(binding.AccessLevel, binding.AccessPermissions, requiredAccess);

        return new AZOAResult<bool> { Result = hasAccess, Message = hasAccess ? "Access verified." : "Insufficient access." };
    }


    private async Task<string?> VerifyNftOwnedByAsync(Guid userNFTId, Guid userId)
    {
        var nftResult = await _nftStore.GetUserNFTByIdAsync(userNFTId, default);
        if (nftResult.IsError || nftResult.Result == null)
            return nftResult.Message ?? "User NFT not found.";
        if (!nftResult.Result.IsActive || !await _lifecycle.IsCurrentOwnerAsync(userId, userNFTId))
            return "Current NFT ownership could not be proven.";
        return null;
    }

    private async Task<UserNFTCompositeResult> BuildCompositeAsync(
        IUserNFT nft,
        IEnumerable<IHolonNFTBinding>? holonBindings,
        IEnumerable<IWalletNFTBinding>? walletBindings)
    {
        var holons = new List<HolonBindingInfo>();
        foreach (var binding in holonBindings ?? [])
        {
            var target = await _holonStore.GetByIdAsync(binding.HolonId);
            if (target.IsError || target.Result is null) continue;
            holons.Add(new HolonBindingInfo { HolonId = binding.HolonId, HolonName = target.Result.Name ?? string.Empty,
                Role = binding.Role, PermissionLevel = binding.PermissionLevel, Permissions = binding.Permissions, IsActive = binding.IsActive });
        }
        var wallets = new List<WalletBindingInfo>();
        foreach (var binding in walletBindings ?? [])
        {
            var target = await _walletStore.GetByIdAsync(binding.WalletId);
            if (target.IsError || target.Result is null) continue;
            wallets.Add(new WalletBindingInfo { WalletId = binding.WalletId, WalletAddress = target.Result.Address,
                ChainType = target.Result.ChainType, BindingType = binding.BindingType, AccessLevel = binding.AccessLevel,
                AccessPermissions = binding.AccessPermissions, IsActive = binding.IsActive });
        }
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
            HolonBindings = holons,
            WalletBindings = wallets,
        };
    }

}
