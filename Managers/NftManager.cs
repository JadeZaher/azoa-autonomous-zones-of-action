using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Managers;

public class NftManager : INftManager
{
    private readonly IHolonStore _holonStore;
    private readonly IBlockchainOperationStore _blockchainOperationStore;
    private readonly IKycGateService _kycGate;

    public NftManager(
        IHolonStore holonStore,
        IBlockchainOperationStore blockchainOperationStore,
        IKycGateService kycGate)
    {
        _holonStore = holonStore;
        _blockchainOperationStore = blockchainOperationStore;
        _kycGate = kycGate ?? throw new ArgumentNullException(nameof(kycGate));
    }

    public async Task<AZOAResult<INft>> GetAsync(Guid id, AZOARequest? request = null)
    {
        var result = await _holonStore.GetByIdAsync(id, default);
        if (result.IsError || result.Result == null) return new AZOAResult<INft> { IsError = true, Message = result.Message };

        if (!string.Equals(result.Result.AssetType, "NFT", StringComparison.OrdinalIgnoreCase))
            return new AZOAResult<INft> { IsError = true, Message = "Holon is not an NFT." };

        return new AZOAResult<INft> { Result = (INft)result.Result, Message = "Success" };
    }

    public async Task<AZOAResult<IEnumerable<INft>>> QueryAsync(NftQueryRequest query, AZOARequest? request = null)
    {
        var all = await _holonStore.QueryAsync(null, default);
        if (all.IsError || all.Result == null) return new AZOAResult<IEnumerable<INft>> { IsError = true, Message = all.Message };

        var filtered = all.Result
            .Where(h => string.Equals(h.AssetType, "NFT", StringComparison.OrdinalIgnoreCase));

        if (query.OwnerAvatarId.HasValue)
            filtered = filtered.Where(h => h.AvatarId == query.OwnerAvatarId.Value);
        if (!string.IsNullOrEmpty(query.ChainId))
            filtered = filtered.Where(h => h.ChainId?.Equals(query.ChainId, StringComparison.OrdinalIgnoreCase) == true);
        if (!string.IsNullOrEmpty(query.TokenId))
            filtered = filtered.Where(h => h.TokenId?.Equals(query.TokenId, StringComparison.OrdinalIgnoreCase) == true);
        if (!string.IsNullOrEmpty(query.Name))
            filtered = filtered.Where(h => h.Name.Contains(query.Name, StringComparison.OrdinalIgnoreCase));

        return new AZOAResult<IEnumerable<INft>> { Result = filtered.Cast<INft>().ToList(), Message = "Success" };
    }

    public async Task<AZOAResult<IBlockchainOperation>> MintAsync(NftMintRequest request, Guid avatarId, AZOARequest? providerRequest = null, Guid? actingTenantId = null)
    {
        // value-path-wiring H3: KYC gate at the single choke point. Both the
        // allocation door and the raw POST /api/nft/mint door pass through here, so
        // gating before ANY side effect (Holon upsert / op build) closes the P5 hole
        // where a tenant could mint via the controller and sidestep the gate. The
        // KYC_FORBIDDEN: prefix is preserved so NftController maps it to 403.
        var gate = await _kycGate.RequireVerifiedAsync(avatarId);
        if (gate.IsError)
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = gate.Message, Exception = gate.Exception };

        // Build the Holon with AssetType = "NFT"
        var metadata = new Dictionary<string, string>(request.Metadata);
        if (!string.IsNullOrEmpty(request.ImageUri)) metadata["image"] = request.ImageUri;
        if (!string.IsNullOrEmpty(request.ExternalUri)) metadata["external_url"] = request.ExternalUri;

        var holon = new Holon
        {
            AvatarId = avatarId,
            Name = request.Name,
            Description = request.Description,
            AssetType = "NFT",
            ChainId = request.ChainId,
            TokenId = request.TokenId,
            Metadata = metadata,
            ProviderName = "PostgreSQL",
            IsActive = true
        };

        var saveResult = await _holonStore.UpsertAsync(holon, default);
        if (saveResult.IsError || saveResult.Result == null)
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = saveResult.Message };

        // Build blockchain operation for mint
        var operation = new BlockchainOperation
        {
            AvatarId = avatarId,
            WalletId = request.WalletId,
            OperationType = "Mint",
            Status = OperationStatus.Pending,
            // tenant-consent-delegation AC4: when a tenant-driven quest Grant node
            // drives this mint, stamp the acting tenant + the nft:mint signing scope
            // so BuildSigningContext marks the op tenant-driven and the custody seam
            // fails closed without a live consent grant. NftMint is the scope a
            // consent grant would name for a mint (vs. transfer:sign for transfers);
            // GrantSign overlaps semantically but Mint is shared with allocation, so
            // the per-operation scope (nft:mint) is the precise, non-overloaded choice.
            ActingTenantId = actingTenantId,
            SigningScope = actingTenantId.HasValue ? AzoaScopes.NftMint : null,
            Parameters = new Dictionary<string, string>
            {
                ["holonId"] = holon.Id.ToString(),
                ["name"] = request.Name,
                ["chainId"] = request.ChainId
            }
        };

        return await _blockchainOperationStore.UpsertAsync(operation, default);
    }

    public async Task<AZOAResult<IBlockchainOperation>> TransferAsync(Guid nftId, NftTransferRequest request, Guid avatarId, AZOARequest? providerRequest = null, Guid? actingTenantId = null)
    {
        // Load and verify ownership
        var holonResult = await _holonStore.GetByIdAsync(nftId, default);
        if (holonResult.IsError || holonResult.Result == null)
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = "NFT not found." };

        var holon = holonResult.Result;
        if (!string.Equals(holon.AssetType, "NFT", StringComparison.OrdinalIgnoreCase))
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = "Holon is not an NFT." };

        if (holon.AvatarId != avatarId)
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = "You do not own this NFT." };

        // Transfer ownership
        holon.AvatarId = request.TargetAvatarId;
        holon.ModifiedDate = DateTime.UtcNow;

        var saveResult = await _holonStore.UpsertAsync(holon, default);
        if (saveResult.IsError)
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = saveResult.Message };

        // Build blockchain operation for transfer
        var operation = new BlockchainOperation
        {
            AvatarId = avatarId,
            WalletId = request.WalletId,
            OperationType = "Transfer",
            Status = OperationStatus.Pending,
            // tenant-consent-delegation AC4: a tenant-driven quest Transfer/Refund
            // node stamps the acting tenant + the transfer:sign scope so the custody
            // seam runs its live consent gate (fail-closed without a grant). The
            // Refund node reuses this path (reversed direction), so transfer:sign
            // correctly covers both.
            ActingTenantId = actingTenantId,
            SigningScope = actingTenantId.HasValue ? AzoaScopes.TransferSign : null,
            Parameters = new Dictionary<string, string>
            {
                ["holonId"] = nftId.ToString(),
                ["fromAvatarId"] = avatarId.ToString(),
                ["toAvatarId"] = request.TargetAvatarId.ToString(),
                ["memo"] = request.Memo ?? string.Empty
            }
        };

        return await _blockchainOperationStore.UpsertAsync(operation, default);
    }

    public async Task<AZOAResult<IBlockchainOperation>> BurnAsync(Guid nftId, Guid walletId, Guid avatarId, AZOARequest? providerRequest = null)
    {
        // Load and verify ownership
        var holonResult = await _holonStore.GetByIdAsync(nftId, default);
        if (holonResult.IsError || holonResult.Result == null)
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = "NFT not found." };

        var holon = holonResult.Result;
        if (!string.Equals(holon.AssetType, "NFT", StringComparison.OrdinalIgnoreCase))
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = "Holon is not an NFT." };

        if (holon.AvatarId != avatarId)
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = "You do not own this NFT." };

        // Burn: deactivate the holon
        holon.IsActive = false;
        holon.ModifiedDate = DateTime.UtcNow;

        var saveResult = await _holonStore.UpsertAsync(holon, default);
        if (saveResult.IsError)
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = saveResult.Message };

        // Build blockchain operation for burn
        var operation = new BlockchainOperation
        {
            AvatarId = avatarId,
            WalletId = walletId,
            OperationType = "Burn",
            Status = OperationStatus.Pending,
            Parameters = new Dictionary<string, string>
            {
                ["holonId"] = nftId.ToString()
            }
        };

        return await _blockchainOperationStore.UpsertAsync(operation, default);
    }

    public async Task<AZOAResult<NftMetadata>> GetMetadataAsync(Guid id, AZOARequest? request = null)
    {
        var result = await _holonStore.GetByIdAsync(id, default);
        if (result.IsError || result.Result == null)
            return new AZOAResult<NftMetadata> { IsError = true, Message = "NFT not found." };

        var holon = result.Result;
        if (!string.Equals(holon.AssetType, "NFT", StringComparison.OrdinalIgnoreCase))
            return new AZOAResult<NftMetadata> { IsError = true, Message = "Holon is not an NFT." };

        var metadata = new NftMetadata
        {
            Name = holon.Name,
            Description = holon.Description
        };

        if (holon.Metadata != null)
        {
            if (holon.Metadata.TryGetValue("image", out var image))
                metadata.Image = image;
            if (holon.Metadata.TryGetValue("external_url", out var externalUrl))
                metadata.ExternalUrl = externalUrl;
            if (holon.Metadata.TryGetValue("animation_url", out var animUrl))
                metadata.AnimationUrl = animUrl;

            // Parse attributes from metadata
            if (holon.Metadata.TryGetValue("attributes", out var attrsJson))
            {
                try
                {
                    var attrs = System.Text.Json.JsonSerializer.Deserialize<List<NftAttribute>>(attrsJson);
                    if (attrs != null) metadata.Attributes = attrs;
                }
                catch
                {
                    // If parsing fails, skip attributes
                }
            }
        }

        return new AZOAResult<NftMetadata> { Result = metadata, Message = "Success" };
    }
}
