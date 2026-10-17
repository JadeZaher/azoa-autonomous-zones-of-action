using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Managers;

/// <inheritdoc/>
public sealed class NftManager(INftStore nfts, IWalletStore wallets, INftLifecycleService lifecycle) : INftManager
{
    /// <inheritdoc/>
    public async Task<AZOAResult<INft>> GetAsync(Guid id, Guid? callerUserId = null, AZOARequest? request = null)
    {
        if (callerUserId is null || !await lifecycle.IsCurrentOwnerAsync(callerUserId.Value, id))
            return AZOAResult<INft>.Failure("NFT not found.");
        var found = await nfts.GetUserNFTByIdAsync(id);
        return found.IsError || found.Result is null ? AZOAResult<INft>.Failure(found.Message)
            : AZOAResult<INft>.Success(Project(found.Result));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<INft>>> QueryAsync(NftQueryRequest query, Guid? callerUserId = null, AZOARequest? request = null)
    {
        if (callerUserId is null || (query.OwnerUserId.HasValue && query.OwnerUserId != callerUserId))
            return AZOAResult<IEnumerable<INft>>.Success([]);
        var found = await nfts.GetUserNFTsByUserAsync(callerUserId.Value);
        if (found.IsError || found.Result is null) return AZOAResult<IEnumerable<INft>>.Failure(found.Message);
        var result = new List<INft>();
        foreach (var nft in found.Result)
        {
            if ((!string.IsNullOrWhiteSpace(query.ChainId) && !string.Equals(query.ChainId, nft.ChainType, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(query.TokenId) && query.TokenId != nft.TokenId)
                || (!string.IsNullOrWhiteSpace(query.Name) && !(nft.Name?.Contains(query.Name, StringComparison.OrdinalIgnoreCase) ?? false))) continue;
            if (await lifecycle.IsCurrentOwnerAsync(callerUserId.Value, nft.Id)) result.Add(Project(nft));
        }
        return AZOAResult<IEnumerable<INft>>.Success(result);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IBlockchainOperation>> MintAsync(NftMintRequest request, Guid userId, AZOARequest? providerRequest = null, Guid? actingTenantId = null)
    {
        if (request is null || userId == Guid.Empty) return AZOAResult<IBlockchainOperation>.Failure("An authenticated NFT mint request is required.");
        var model = NftLifecycleOperationProjection.MintModel(request, providerRequest?.NftFeeAuthorization);
        var result = providerRequest?.NftAdmissionOnly == true
            ? await lifecycle.AdmitMintAsync(userId, userId, model, providerRequest.IdempotencyKey ?? string.Empty, actingTenantId: actingTenantId ?? Guid.Empty, actionAuthority: providerRequest?.ActionAuthority)
            : await lifecycle.MintAsync(userId, userId, model, providerRequest?.IdempotencyKey ?? string.Empty, actingTenantId: actingTenantId ?? Guid.Empty, actionAuthority: providerRequest?.ActionAuthority, credential: providerRequest?.AuthenticationWitness);
        return NftLifecycleOperationProjection.Project(result, userId, request.WalletId, request.ChainId);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IBlockchainOperation>> TransferAsync(Guid nftId, NftTransferRequest request, Guid userId,
        AZOARequest? providerRequest = null, Guid? actingTenantId = null)
    {
        if (request is null || userId == Guid.Empty) return AZOAResult<IBlockchainOperation>.Failure("An authenticated NFT transfer request is required.");
        var nft = await nfts.GetUserNFTByIdAsync(nftId);
        if (nft.IsError || nft.Result is null) return AZOAResult<IBlockchainOperation>.Failure("NFT not found.");
        var source = await wallets.GetByIdAsync(request.WalletId);
        if (source.IsError || source.Result is null || source.Result.UserId != userId
            || !source.Result.ChainType.Equals(nft.Result.ChainType, StringComparison.OrdinalIgnoreCase))
            return AZOAResult<IBlockchainOperation>.Failure("The selected signing wallet is unavailable.");
        var candidates = await wallets.GetByUserAsync(request.TargetUserId);
        var destination = candidates.Result?.FirstOrDefault(wallet => wallet.ChainType.Equals(nft.Result.ChainType, StringComparison.OrdinalIgnoreCase));
        if (destination is null) return AZOAResult<IBlockchainOperation>.Failure("Recipient signing wallet is unavailable.");
        var moved = providerRequest?.NftAdmissionOnly == true
            ? await lifecycle.AdmitMoveAsync(userId, nftId, destination.Address, false,
                providerRequest.IdempotencyKey ?? string.Empty, actingTenantId: actingTenantId ?? Guid.Empty,
                feeAuthorization: request.FeeAuthorization ?? providerRequest.NftFeeAuthorization, sourceWalletId: request.WalletId, actionAuthority: providerRequest?.ActionAuthority)
            : await lifecycle.MoveAsync(userId, nftId, destination.Address, false,
            providerRequest?.IdempotencyKey ?? string.Empty, actingTenantId: actingTenantId ?? Guid.Empty,
            feeAuthorization: request.FeeAuthorization ?? providerRequest?.NftFeeAuthorization, sourceWalletId: request.WalletId, actionAuthority: providerRequest?.ActionAuthority, credential: providerRequest?.AuthenticationWitness);
        return NftLifecycleOperationProjection.Project(moved, userId, request.WalletId, nft.Result.ChainType);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IBlockchainOperation>> BurnAsync(Guid nftId, Guid walletId, Guid userId,
        AZOARequest? providerRequest = null, Guid? actingTenantId = null)
    {
        var nft = await nfts.GetUserNFTByIdAsync(nftId);
        var source = await wallets.GetByIdAsync(walletId);
        if (nft.IsError || nft.Result is null || source.IsError || source.Result is null
            || source.Result.UserId != userId || !source.Result.ChainType.Equals(nft.Result.ChainType, StringComparison.OrdinalIgnoreCase))
            return AZOAResult<IBlockchainOperation>.Failure("The selected wallet does not own this NFT.");
        var moved = providerRequest?.NftAdmissionOnly == true
            ? await lifecycle.AdmitMoveAsync(userId, nftId, null, true,
                providerRequest.IdempotencyKey ?? string.Empty, actingTenantId: actingTenantId ?? Guid.Empty,
                feeAuthorization: providerRequest.NftFeeAuthorization, sourceWalletId: walletId, actionAuthority: providerRequest?.ActionAuthority)
            : await lifecycle.MoveAsync(userId, nftId, null, true,
            providerRequest?.IdempotencyKey ?? string.Empty, actingTenantId: actingTenantId ?? Guid.Empty,
            feeAuthorization: providerRequest?.NftFeeAuthorization, sourceWalletId: walletId, actionAuthority: providerRequest?.ActionAuthority, credential: providerRequest?.AuthenticationWitness);
        return NftLifecycleOperationProjection.Project(moved, userId, walletId, nft.Result.ChainType);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NftMetadata>> GetMetadataAsync(Guid id, AZOARequest? request = null, Guid? callerUserId = null)
    {
        var found = await GetAsync(id, callerUserId, request);
        if (found.IsError || found.Result is null) return AZOAResult<NftMetadata>.Failure(found.Message);
        var nft = found.Result;
        return AZOAResult<NftMetadata>.Success(new NftMetadata
        {
            Name = nft.Name, Description = nft.Description,
            Image = nft.Metadata.GetValueOrDefault("image"), ExternalUrl = nft.Metadata.GetValueOrDefault("external_url"),
        });
    }

    private static INft Project(IUserNFT nft) => new Holon
    {
        Id = nft.Id, UserId = nft.UserId, Name = nft.Name ?? string.Empty, Description = nft.Description ?? string.Empty,
        AssetType = "NFT", ChainId = nft.ChainType, TokenId = nft.TokenId, ProviderName = "SurrealDB",
        IsActive = nft.IsActive, CreatedDate = nft.MintedDate, ModifiedDate = nft.LastTransferDate,
        Metadata = new Dictionary<string, string>(nft.Attributes)
        {
            ["image"] = nft.ImageURI ?? string.Empty, ["metadata_uri"] = nft.MetadataURI,
        },
    };

}
