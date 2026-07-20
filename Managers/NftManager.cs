using System.Globalization;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Managers;

public class NftManager : INftManager
{
    private readonly IHolonStore _holonStore;
    private readonly IBlockchainOperationStore _blockchainOperationStore;
    private readonly IValueAccessService _valueAccess;
    private readonly INodeFeeScheduleManager _nodeFees;
    private readonly INodeGovernanceGuard _nodeGovernance;

    public NftManager(
        IHolonStore holonStore,
        IBlockchainOperationStore blockchainOperationStore,
        IValueAccessService valueAccess,
        INodeFeeScheduleManager nodeFees,
        INodeGovernanceGuard nodeGovernance)
    {
        _holonStore = holonStore;
        _blockchainOperationStore = blockchainOperationStore;
        _valueAccess = valueAccess ?? throw new ArgumentNullException(nameof(valueAccess));
        _nodeFees = nodeFees ?? throw new ArgumentNullException(nameof(nodeFees));
        _nodeGovernance = nodeGovernance ?? throw new ArgumentNullException(nameof(nodeGovernance));
    }

    // Cross-tenant read scope: an NFT (a Holon with AssetType=="NFT") is readable iff
    // the caller owns it OR the underlying holon is IsPublic. A null callerAvatarId
    // fails closed (public-only). See Controllers/AGENTS.md §cross-tenant-read-scope.
    private static bool CanRead(IHolon holon, Guid? callerAvatarId) =>
        callerAvatarId.HasValue && holon.AvatarId == callerAvatarId.Value || holon.IsPublic;

    public async Task<AZOAResult<INft>> GetAsync(Guid id, Guid? callerAvatarId = null, AZOARequest? request = null)
    {
        var result = await _holonStore.GetByIdAsync(id, default);
        if (result.IsError || result.Result == null) return new AZOAResult<INft> { IsError = true, Message = result.Message };

        if (!string.Equals(result.Result.AssetType, "NFT", StringComparison.OrdinalIgnoreCase))
            return new AZOAResult<INft> { IsError = true, Message = "Holon is not an NFT." };

        // Own-or-public gate: don't confirm a private NFT's existence to a non-owner.
        if (!CanRead(result.Result, callerAvatarId))
            return new AZOAResult<INft> { IsError = true, Message = "NFT not found." };

        return new AZOAResult<INft> { Result = (INft)result.Result, Message = "Success" };
    }

    public async Task<AZOAResult<IEnumerable<INft>>> QueryAsync(NftQueryRequest query, Guid? callerAvatarId = null, AZOARequest? request = null)
    {
        var all = await _holonStore.QueryAsync(null, default);
        if (all.IsError || all.Result == null) return new AZOAResult<IEnumerable<INft>> { IsError = true, Message = all.Message };

        var filtered = all.Result
            .Where(h => string.Equals(h.AssetType, "NFT", StringComparison.OrdinalIgnoreCase))
            // Force owner-or-public FIRST — the caller-supplied query.OwnerAvatarId can
            // only ever narrow WITHIN what the caller may already read (no cross-tenant
            // enumeration by supplying another avatar's id).
            .Where(h => CanRead(h, callerAvatarId));

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

    /// <inheritdoc/>
    public async Task<AZOAResult<IBlockchainOperation>> MintAsync(NftMintRequest request, Guid avatarId, AZOARequest? providerRequest = null, Guid? actingTenantId = null)
    {
        return await MintCoreAsync(request, avatarId, actingTenantId);
    }

    private async Task<AZOAResult<IBlockchainOperation>> MintCoreAsync(
        NftMintRequest request,
        Guid avatarId,
        Guid? actingTenantId)
    {
        var governance = await _nodeGovernance.EnsureAllowedAsync(request.ChainId, "NFT", "nft:mint");
        if (governance.IsError)
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = governance.Message };

        var mintFeeBlocker = await GetDirectFeeBlockerAsync(NodeFeeOperation.Mint);
        if (mintFeeBlocker is not null)
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = mintFeeBlocker };

        // Value readiness is mandatory before a mint writes its Holon or operation.
        var gate = await _valueAccess.RequireValueAccessAsync(avatarId, actingTenantId);
        if (gate.IsError)
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = gate.Message, Exception = gate.Exception };

        var holon = NftHolonFactory.Create(request, avatarId);

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

    /// <inheritdoc/>
    public async Task<AZOAResult<IBlockchainOperation>> TransferAsync(Guid nftId, NftTransferRequest request, Guid avatarId, AZOARequest? providerRequest = null, Guid? actingTenantId = null)
    {
        var transferFeeBlocker = await GetDirectFeeBlockerAsync(NodeFeeOperation.Transfer);
        if (transferFeeBlocker is not null)
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = transferFeeBlocker };

        var gate = await _valueAccess.RequireValueAccessAsync(avatarId, actingTenantId);
        if (gate.IsError)
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = gate.Message, Exception = gate.Exception };

        // Load and verify ownership
        var holonResult = await _holonStore.GetByIdAsync(nftId, default);
        if (holonResult.IsError || holonResult.Result == null)
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = "NFT not found." };

        var holon = holonResult.Result;
        if (!string.Equals(holon.AssetType, "NFT", StringComparison.OrdinalIgnoreCase))
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = "Holon is not an NFT." };

        var governance = await _nodeGovernance.EnsureAllowedAsync(holon.ChainId, "NFT", "nft:transfer");
        if (governance.IsError)
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = governance.Message };

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

    private async Task<string?> GetDirectFeeBlockerAsync(NodeFeeOperation operation)
    {
        var schedule = await _nodeFees.GetScheduleAsync();
        if (schedule.IsError || schedule.Result is null)
        {
            return $"Node {operation} fee schedule is unavailable; direct NFT {operation.ToString().ToLowerInvariant()} denied until fee settlement can be verified.";
        }

        var entry = operation == NodeFeeOperation.Mint ? schedule.Result.Mint : schedule.Result.Transfer;
        if (!ulong.TryParse(entry.FlatBaseUnits, NumberStyles.None, CultureInfo.InvariantCulture, out var flat)
            || entry.Bps is < 0 or > 10_000)
        {
            return $"Node {operation} fee schedule is invalid; direct NFT {operation.ToString().ToLowerInvariant()} denied.";
        }

        return flat > 0 || entry.Bps > 0
            ? $"Direct NFT {operation.ToString().ToLowerInvariant()} is unavailable while a nonzero node {operation} fee requires on-chain treasury settlement."
            : null;
    }

    public async Task<AZOAResult<IBlockchainOperation>> BurnAsync(Guid nftId, Guid walletId, Guid avatarId, AZOARequest? providerRequest = null)
    {
        var gate = await _valueAccess.RequireValueAccessAsync(avatarId);
        if (gate.IsError)
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = gate.Message, Exception = gate.Exception };

        // Load and verify ownership
        var holonResult = await _holonStore.GetByIdAsync(nftId, default);
        if (holonResult.IsError || holonResult.Result == null)
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = "NFT not found." };

        var holon = holonResult.Result;
        if (!string.Equals(holon.AssetType, "NFT", StringComparison.OrdinalIgnoreCase))
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = "Holon is not an NFT." };

        var governance = await _nodeGovernance.EnsureAllowedAsync(holon.ChainId, "NFT", "nft:burn");
        if (governance.IsError)
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = governance.Message };

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
