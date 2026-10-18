using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Base;

namespace AZOA.WebAPI.Managers;

public class WalletManager : IWalletManager
{
    private readonly IWalletStore _walletStore;
    private readonly IHolonStore _holonStore;
    private readonly IBlockchainProviderFactory _chainFactory;
    private readonly WalletKeyService _keyService;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment? _environment;
    private readonly BlockchainConfigurationManager _blockchainConfig;
    private readonly Func<Guid> _entityId;
    private readonly IWalletAuthManager? _walletAuth;
    private readonly IWalletOwnershipProofStore? _ownershipProofs;
    private readonly ILogger<WalletManager>? _logger;
    private const string OwnershipProofUnavailable = "Wallet ownership proof is unavailable on this node.";

    /// <param name="environment">
    /// Host environment the custody predicate reads. Optional so the manager can be
    /// constructed outside a host; absent is treated as "not Development", which makes
    /// <see cref="CustodyModeResolver"/> fail closed rather than open.
    /// </param>
    public WalletManager(
        IWalletStore walletStore,
        IHolonStore holonStore,
        IBlockchainProviderFactory chainFactory,
        WalletKeyService keyService,
        IConfiguration config,
        IHostEnvironment? environment = null,
        Func<Guid>? entityId = null,
        IWalletAuthManager? walletAuth = null,
        IWalletOwnershipProofStore? ownershipProofs = null,
        ILogger<WalletManager>? logger = null)
    {
        _walletStore = walletStore;
        _holonStore = holonStore;
        _chainFactory = chainFactory;
        _keyService = keyService;
        _config = config;
        _environment = environment;
        _blockchainConfig = new BlockchainConfigurationManager(config);
        _entityId = entityId ?? Guid.NewGuid;
        _walletAuth = walletAuth;
        _ownershipProofs = ownershipProofs;
        _logger = logger;
    }

    public async Task<AZOAResult<IWallet>> GetAsync(Guid id, Guid userId, AZOARequest? request = null)
    {
        var result = await _walletStore.GetByIdAsync(id, default);
        if (result.IsError || result.Result == null) return result;

        if (result.Result.UserId != userId)
            return new AZOAResult<IWallet> { IsError = true, Message = "Wallet not found." };

        return result;
    }

    public async Task<AZOAResult<IEnumerable<IWallet>>> QueryAsync(WalletQueryRequest query, Guid userId, AZOARequest? request = null)
    {
        var all = await _walletStore.GetAllAsync(default);
        if (all.IsError || all.Result == null) return all;

        // Force the filter to the authenticated user — never trust query.UserId.
        var filtered = all.Result.Where(w => w.UserId == userId);

        if (!string.IsNullOrEmpty(query.ChainType))
            filtered = filtered.Where(w => w.ChainType.Equals(query.ChainType, StringComparison.OrdinalIgnoreCase));
        if (query.IsDefault.HasValue)
            filtered = filtered.Where(w => w.IsDefault == query.IsDefault.Value);

        return new AZOAResult<IEnumerable<IWallet>> { Result = filtered.ToList(), Message = "Success" };
    }

    public async Task<AZOAResult<IWallet>> CreateAsync(WalletCreateModel model, Guid userId, AZOARequest? request = null)
    {
        // Unproven rows grant nothing, so only the caller's own row or another user's proof blocks (audit-1 M2).
        var owned = await _walletStore.GetByUserAsync(userId, default);
        if (owned.IsError || owned.Result is null) return AZOAResult<IWallet>.Failure(owned.Message);
        if (owned.Result.Any(w => w.ChainType.Equals(model.ChainType, StringComparison.OrdinalIgnoreCase)
            && ChainAddressIdentity.Equivalent(model.ChainType, w.Address, model.Address)))
            return new AZOAResult<IWallet> { IsError = true, Message = "Wallet address already exists for this chain." };
        if (await ProvenByAnotherUserAsync(model.ChainType, model.Address, userId))
            return new AZOAResult<IWallet> { IsError = true, Message = ProvenElsewhere };

        var wallet = new Wallet
        {
            Id = _entityId(),
            UserId = userId,
            ChainType = model.ChainType,
            Address = model.Address,
            PublicKey = model.PublicKey,
            Label = model.Label,
            IsDefault = model.IsDefault,
            WalletType = WalletType.External
        };

        if (model.IsDefault)
        {
            await UnsetPreviousDefaultAsync(userId, model.ChainType, wallet.Id);
        }

        return await _walletStore.UpsertAsync(wallet, default);
    }

    public async Task<AZOAResult<IWallet>> UpdateAsync(Guid id, WalletUpdateModel model, Guid userId, AZOARequest? request = null)
    {
        var existing = await _walletStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null) return existing;

        if (existing.Result.UserId != userId)
            return new AZOAResult<IWallet> { IsError = true, Message = "Wallet not found." };

        var wallet = (Wallet)existing.Result;
        if (model.Label != null) wallet.Label = model.Label;

        if (model.IsDefault.HasValue && model.IsDefault.Value && !wallet.IsDefault)
        {
            await UnsetPreviousDefaultAsync(wallet.UserId, wallet.ChainType, wallet.Id);
            wallet.IsDefault = true;
        }
        else if (model.IsDefault.HasValue)
        {
            wallet.IsDefault = model.IsDefault.Value;
        }

        return await _walletStore.UpsertAsync(wallet, default);
    }

    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid userId, AZOARequest? request = null)
    {
        var existing = await _walletStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = "Wallet not found." };

        if (existing.Result.UserId != userId)
            return new AZOAResult<bool> { IsError = true, Message = "Wallet not found." };

        return await _walletStore.DeleteAsync(id, default);
    }

    public async Task<AZOAResult<bool>> SetDefaultAsync(Guid userId, Guid walletId, AZOARequest? request = null)
    {
        var walletResult = await _walletStore.GetByIdAsync(walletId, default);
        if (walletResult.IsError || walletResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = "Wallet not found." };

        var wallet = walletResult.Result;
        if (wallet.UserId != userId)
            return new AZOAResult<bool> { IsError = true, Message = "Wallet not owned by user." };

        await UnsetPreviousDefaultAsync(userId, wallet.ChainType, walletId);

        wallet.IsDefault = true;
        var saveResult = await _walletStore.UpsertAsync(wallet, default);
        if (saveResult.IsError)
            return new AZOAResult<bool> { IsError = true, Message = saveResult.Message };

        return new AZOAResult<bool> { Result = true, Message = "Default wallet set." };
    }

    public async Task<AZOAResult<PortfolioResult>> GetPortfolioAsync(Guid walletId, Guid userId, AZOARequest? request = null)
    {
        var walletResult = await _walletStore.GetByIdAsync(walletId, default);
        if (walletResult.IsError || walletResult.Result == null)
            return new AZOAResult<PortfolioResult> { IsError = true, Message = "Wallet not found." };

        var wallet = walletResult.Result;

        if (wallet.UserId != userId)
            return new AZOAResult<PortfolioResult> { IsError = true, Message = "Wallet not found." };

        // Stub: linked NFT Holons for this user
        var allHolons = await _holonStore.QueryAsync(null, default);
        var nfts = allHolons.Result?
            .Where(h => h.UserId == wallet.UserId && h.AssetType == "NFT")
            .Select(h => new NftHolding
            {
                HolonId = h.Id,
                Name = h.Name,
                TokenId = h.TokenId,
                ImageUri = h.Metadata.TryGetValue("image", out var img) ? img : null
            })
            .ToList() ?? new List<NftHolding>();

        var symbol = wallet.ChainType.ToUpperInvariant() switch
        {
            "ALGORAND" or "ALGO" => "ALGO",
            "SOLANA" or "SOL" => "SOL",
            "ETHEREUM" or "ETH" => "ETH",
            _ => wallet.ChainType.ToUpperInvariant()
        };

        decimal balance = 0;
        var fungibles = new List<FungibleHolding>();
        try
        {
            var network = _blockchainConfig.GetDefaultNetwork(wallet.ChainType);
            var chainProvider = _chainFactory.GetProvider(wallet.ChainType, network);
            if (chainProvider != null)
            {
                var balanceResult = await chainProvider.GetBalanceAsync(wallet.Address);
                if (!balanceResult.IsError && balanceResult.Result != null)
                    decimal.TryParse(balanceResult.Result, out balance);

                // Fungible (ASA) holdings from chain truth: enumerate owned tokens via
                // the existing provider surface (GetTokensByOwnerAsync gives assetId +
                // base-unit amount), then hydrate display metadata (unit name, name,
                // decimals) per asset via GetTokenMetadataAsync — the SAME path the
                // fungible-mint flow reads. No new chain call invented.
                fungibles = await GatherFungibleHoldingsAsync(chainProvider, wallet.Address);
            }
        }
        catch (Exception ex)
        {
            // Fall back to 0 / empty if blockchain unavailable.
            System.Diagnostics.Debug.WriteLine($"Portfolio balance lookup failed for wallet {wallet.Id}: {ex.Message}");
        }

        // Render-ready asset list (fungible-mint-and-render-model §11.5): shape chain
        // truth into everything the frontend needs in ONE call — the native coin, every
        // fungible (ASA) holding, and every NFT holding, each carrying raw +
        // display-formatted amounts so the UI renders without a second round-trip or
        // client-side decimals math.
        var assets = BuildRenderAssets(wallet.ChainType, symbol, balance, fungibles, nfts);

        var portfolio = new PortfolioResult
        {
            WalletId = wallet.Id,
            ChainType = wallet.ChainType,
            Address = wallet.Address,
            Balance = balance,
            Symbol = symbol,
            Nfts = nfts,
            Assets = assets,
            ComputedAt = DateTime.UtcNow
        };

        return new AZOAResult<PortfolioResult> { Result = portfolio, Message = "Portfolio computed." };
    }

    // ─── Render-model assembly (fungible-mint-and-render-model §11.5) ───

    /// <summary>
    /// Shapes chain truth into the unified, render-ready <see cref="PortfolioAsset"/>
    /// list: the native coin (decimal-adjusted display + base-unit raw), one entry per
    /// fungible (ASA) holding, plus one entry per NFT holding. The frontend renders
    /// directly off this — no second round-trip, no client-side decimals math.
    /// </summary>
    private static List<PortfolioAsset> BuildRenderAssets(
        string chainType, string symbol, decimal nativeDisplayBalance,
        List<FungibleHolding> fungibles, List<NftHolding> nfts)
    {
        var assets = new List<PortfolioAsset>();

        var nativeDecimals = Amounts.NativeDecimalsFor(chainType);
        assets.Add(new PortfolioAsset
        {
            Id = symbol,
            Kind = PortfolioAssetKind.Native,
            Symbol = symbol,
            Name = chainType,
            Decimals = nativeDecimals,
            // The provider balance is already in whole-coin (display) units; derive the
            // base-unit raw amount from the chain's native decimals.
            RawAmount = Amounts.ToBaseUnits(nativeDisplayBalance, nativeDecimals),
            DisplayAmount = nativeDisplayBalance.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Chain = chainType,
            IconRef = null
        });

        // Fungible (ASA) holdings: RawAmount is chain-truth base units; DisplayAmount is
        // derived from the asset's own decimals (NOT the native chain decimals).
        foreach (var fungible in fungibles)
        {
            assets.Add(new PortfolioAsset
            {
                Id = fungible.AssetId,
                Kind = PortfolioAssetKind.Fungible,
                Symbol = string.IsNullOrWhiteSpace(fungible.UnitName) ? fungible.AssetId : fungible.UnitName,
                Name = string.IsNullOrWhiteSpace(fungible.Name) ? fungible.AssetId : fungible.Name,
                Decimals = fungible.Decimals,
                RawAmount = fungible.RawAmount,
                DisplayAmount = Amounts.FromBaseUnits(fungible.RawAmount, fungible.Decimals),
                Chain = chainType,
                IconRef = null
            });
        }

        foreach (var nft in nfts)
        {
            assets.Add(new PortfolioAsset
            {
                Id = nft.HolonId.ToString(),
                Kind = PortfolioAssetKind.Nft,
                // NFTs are supply-1, no unit name; the token id (when present) is the
                // closest symbol-like handle, else fall back to the name.
                Symbol = nft.TokenId ?? nft.Name,
                Name = nft.Name,
                Decimals = 0,
                RawAmount = "1",
                DisplayAmount = "1",
                Chain = chainType,
                IconRef = nft.ImageUri
            });
        }

        return assets;
    }

    /// <summary>
    /// Enumerates the wallet's fungible-token (ASA) holdings from chain truth using the
    /// existing provider surface: <c>GetTokensByOwnerAsync</c> for the owned
    /// asset-id/base-unit pairs, then <c>GetTokenMetadataAsync</c> per asset to hydrate
    /// the unit name / name / decimals. Zero-amount rows are skipped. Any per-asset
    /// metadata failure degrades gracefully (the holding is still emitted with 0
    /// decimals and the asset id as its label) rather than dropping the holding or
    /// throwing into the portfolio path.
    /// </summary>
    private static async Task<List<FungibleHolding>> GatherFungibleHoldingsAsync(
        IBlockchainProvider provider, string ownerAddress)
    {
        var holdings = new List<FungibleHolding>();

        var tokensResult = await provider.GetTokensByOwnerAsync(ownerAddress);
        if (tokensResult.IsError || tokensResult.Result == null)
            return holdings;

        foreach (var token in tokensResult.Result)
        {
            var assetId = token.TryGetValue("assetId", out var idObj) ? idObj?.ToString() ?? "" : "";
            var rawAmount = token.TryGetValue("amount", out var amtObj) ? amtObj?.ToString() ?? "0" : "0";
            if (string.IsNullOrWhiteSpace(assetId))
                continue;

            // Skip zero-balance ASAs (an opted-in-but-empty holding is not a holding).
            if (System.Numerics.BigInteger.TryParse(rawAmount, out var rawValue) && rawValue.IsZero)
                continue;

            var holding = new FungibleHolding
            {
                AssetId = assetId,
                RawAmount = rawAmount,
                UnitName = string.Empty,
                Name = assetId,
                Decimals = 0
            };

            var metaResult = await provider.GetTokenMetadataAsync(assetId);
            if (!metaResult.IsError && metaResult.Result != null)
            {
                var meta = metaResult.Result;
                if (meta.TryGetValue("unitName", out var unit) && unit is not null)
                    holding.UnitName = unit.ToString() ?? string.Empty;
                if (meta.TryGetValue("name", out var name) && name is not null && !string.IsNullOrWhiteSpace(name.ToString()))
                    holding.Name = name.ToString()!;
                if (meta.TryGetValue("decimals", out var dec) && dec is not null
                    && int.TryParse(dec.ToString(), out var decimals))
                    holding.Decimals = decimals;
            }

            holdings.Add(holding);
        }

        return holdings;
    }

    // ─── New: Generate a wallet on-platform ───

    /// <summary>Stable prefix callers match on when custody refuses a new wallet.</summary>
    public const string WalletCustodyUnavailable = "WALLET_CUSTODY_UNAVAILABLE: ";

    /// <summary>
    /// Generates and stores a platform-custodied wallet. Every wallet-creating path that
    /// is not the deterministic tenant bootstrap funnels through here — the direct
    /// <c>POST /api/wallet/generate</c> route, <c>AllocationManager.EnsureWalletAsync</c>
    /// and <c>FungibleTokenManager.EnsureWalletAsync</c> — so the custody predicate is
    /// answered once, here, instead of three times at the call sites.
    /// </summary>
    public async Task<AZOAResult<IWallet>> GenerateWalletAsync(WalletGenerateRequest model, Guid userId, AZOARequest? request = null)
    {
        var custody = CustodyModeResolver.Resolve(_config, _environment);
        if (!custody.Available)
            return AZOAResult<IWallet>.Failure($"{WalletCustodyUnavailable}{custody.UnavailableReason}");

        try
        {
            var (publicKey, privateKeyHex, address, seedPhrase) = _keyService.GenerateKeypair(model.ChainType);

            // Check uniqueness
            var all = await _walletStore.GetAllAsync(default);
            var existing = all.Result?.FirstOrDefault(w =>
                w.Address.Equals(address, StringComparison.OrdinalIgnoreCase) &&
                w.ChainType.Equals(model.ChainType, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
                return new AZOAResult<IWallet> { IsError = true, Message = "Generated address collision — please retry." };

            var wallet = new Wallet
            {
                UserId = userId,
                ChainType = model.ChainType,
                Address = address,
                PublicKey = publicKey,
                Label = model.Label,
                IsDefault = model.IsDefault,
                WalletType = WalletType.Platform,
                EncryptedPrivateKey = _keyService.EncryptPrivateKey(privateKeyHex),
                EncryptedSeedPhrase = seedPhrase != null ? _keyService.EncryptSeedPhrase(seedPhrase) : null
            };

            if (model.IsDefault)
                await UnsetPreviousDefaultAsync(userId, model.ChainType, wallet.Id);

            return await _walletStore.UpsertAsync(wallet, default);
        }
        catch (NotSupportedException ex)
        {
            return new AZOAResult<IWallet> { IsError = true, Message = ex.Message };
        }
    }

    /// <summary>
    /// Provisions the deterministic tenant custodial wallet, create-only and idempotent.
    /// Answers the custody predicate HERE, at the mint, exactly as
    /// <see cref="GenerateWalletAsync"/> does.
    /// </summary>
    /// <remarks>
    /// OI-12. This method is on the public <see cref="IWalletManager"/> surface, so "the sole
    /// caller already checked" is a property of today's call graph, not of the contract: a
    /// second caller - a controller, a worker, another manager - inherits nothing from
    /// <c>TenantCustodialAccountManager.ConvergeAccountAsync</c>. The gate therefore sits
    /// where the key is minted, so a node configured with CustodyMode=Disabled cannot mint
    /// one through any door.
    /// </remarks>
    public async Task<AZOAResult<IWallet>> BootstrapWalletAsync(WalletGenerateRequest model, Guid userId, AZOARequest? request = null)
    {
        var custody = CustodyModeResolver.Resolve(_config, _environment);
        if (!custody.Available)
            return AZOAResult<IWallet>.Failure($"{WalletCustodyUnavailable}{custody.UnavailableReason}");

        if (userId == Guid.Empty)
            return AZOAResult<IWallet>.Failure("An authenticated user is required.");

        var chainType = WalletBootstrapIdentity.CanonicalChain(model.ChainType);
        if (chainType is null)
            return AZOAResult<IWallet>.Failure($"Chain type '{model.ChainType}' is not supported for wallet generation.");

        var walletId = WalletBootstrapIdentity.For(userId, chainType);
        var existing = await _walletStore.GetByIdAsync(walletId, default);
        if (!existing.IsError && existing.Result is not null)
            return ValidateBootstrapWallet(existing.Result, userId, chainType);

        try
        {
            var candidate = CreateBootstrapCandidate(model, userId, chainType, walletId);

            var created = await _walletStore.CreateIfAbsentAsync(candidate, default);
            if (created.IsError || created.Result is null)
                return created;

            if (created.Result.IsDefault)
                await UnsetPreviousDefaultAsync(userId, chainType, walletId);

            return ValidateBootstrapWallet(created.Result, userId, chainType);
        }
        catch (NotSupportedException ex)
        {
            return AZOAResult<IWallet>.Failure(ex.Message);
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IWallet>> BootstrapTenantWalletAsync(WalletGenerateRequest model, Guid userId,
        string externalUserId, SigningConsentWitness authority, CancellationToken ct = default)
    {
        var custody = CustodyModeResolver.Resolve(_config, _environment);
        if (!custody.Available) return AZOAResult<IWallet>.Failure($"{WalletCustodyUnavailable}{custody.UnavailableReason}");
        var chain = WalletBootstrapIdentity.CanonicalChain(model.ChainType);
        if (userId == Guid.Empty || chain is null) return AZOAResult<IWallet>.Failure("A valid child and chain are required.");
        var id = WalletBootstrapIdentity.For(userId, chain);
        var existing = await _walletStore.GetByIdAsync(id, ct);
        if (existing.IsError && existing.Message != "Wallet not found.") return existing;
        try
        {
            var candidate = existing.Result ?? CreateBootstrapCandidate(model, userId, chain, id);
            var saved = await _walletStore.BootstrapTenantAsync(candidate, externalUserId, authority, ct);
            return saved.IsError || saved.Result is null ? saved : ValidateBootstrapWallet(saved.Result, userId, chain);
        }
        catch (NotSupportedException exception) { return AZOAResult<IWallet>.Failure(exception.Message); }
    }

    private Wallet CreateBootstrapCandidate(WalletGenerateRequest model, Guid userId, string chainType, Guid walletId)
    {
        var (publicKey, privateKeyHex, address) = _keyService.GenerateCustodialKeypair(chainType);
        return new Wallet
        {
            Id = walletId, UserId = userId, ChainType = chainType, Address = address, PublicKey = publicKey,
            Label = string.IsNullOrWhiteSpace(model.Label) ? $"{chainType} custodial wallet" : model.Label,
            IsDefault = model.IsDefault, WalletType = WalletType.Platform,
            EncryptedPrivateKey = _keyService.EncryptPrivateKey(privateKeyHex), EncryptedSeedPhrase = null,
        };
    }

    private static AZOAResult<IWallet> ValidateBootstrapWallet(IWallet wallet, Guid userId, string chainType)
    {
        if (wallet.UserId != userId
            || wallet.WalletType != WalletType.Platform
            || !string.Equals(wallet.ChainType, chainType, StringComparison.OrdinalIgnoreCase))
        {
            return AZOAResult<IWallet>.Failure("The deterministic wallet id is already bound to a different wallet.");
        }

        return AZOAResult<IWallet>.Success(wallet, "Platform wallet is ready.");
    }

    // ─── New: Connect an external wallet (MetaMask, Ghost, etc.) ───

    public async Task<AZOAResult<IWallet>> ConnectWalletAsync(WalletConnectRequest model, Guid userId, AZOARequest? request = null)
    {
        if (string.IsNullOrWhiteSpace(model.Address))
            return new AZOAResult<IWallet> { IsError = true, Message = "Address is required." };

        var owned = await _walletStore.GetByUserAsync(userId, default);
        if (owned.IsError || owned.Result is null) return AZOAResult<IWallet>.Failure(owned.Message);
        var existing = owned.Result.FirstOrDefault(w => w.ChainType.Equals(model.ChainType, StringComparison.OrdinalIgnoreCase)
            && ChainAddressIdentity.Equivalent(model.ChainType, w.Address, model.Address));
        // RV-L4-03: proofs bind to a purpose-bound challenge naming the wallet, so they follow connect (ownership-challenge + ownership-proof).
        if (!string.IsNullOrWhiteSpace(model.SignedMessage))
            return AZOAResult<IWallet>.Failure(InlineProofRetired);
        if (existing is not null)
            return new AZOAResult<IWallet> { Result = existing, Message = "Wallet already connected." };
        if (await ProvenByAnotherUserAsync(model.ChainType, model.Address, userId))
            return new AZOAResult<IWallet> { IsError = true, Message = ProvenElsewhere };
        return await CreateExternalAsync(model, userId);
    }

    private const string InlineProofRetired =
        "Inline ownership proofs are retired; connect the wallet, then prove it via POST /api/wallet/{id}/ownership-challenge and ownership-proof.";

    private async Task<AZOAResult<IWallet>> CreateExternalAsync(WalletConnectRequest model, Guid userId)
    {
        var wallet = new Wallet
        {
            UserId = userId,
            ChainType = model.ChainType,
            Address = model.Address,
            PublicKey = model.PublicKey,
            Label = model.Label,
            IsDefault = model.IsDefault,
            WalletType = WalletType.External
        };

        if (model.IsDefault)
            await UnsetPreviousDefaultAsync(userId, model.ChainType, wallet.Id);

        return await _walletStore.UpsertAsync(wallet, default);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<WalletChallengeResponse>> CreateOwnershipChallengeAsync(Guid walletId, Guid userId, CancellationToken ct = default)
    {
        var wallet = await OwnedExternalWalletAsync(walletId, userId, ct);
        if (wallet.IsError || wallet.Result is null) return AZOAResult<WalletChallengeResponse>.Failure(wallet.Message);
        if (_walletAuth is null || _ownershipProofs is null) return AZOAResult<WalletChallengeResponse>.Failure(OwnershipProofUnavailable);
        return await _walletAuth.CreateOwnershipChallengeAsync(userId, walletId, wallet.Result.Address, wallet.Result.ChainType, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<WalletOwnershipProof>> ProveOwnershipAsync(Guid walletId, Guid userId, string signature, string? message,
        CancellationToken ct = default)
    {
        var wallet = await OwnedExternalWalletAsync(walletId, userId, ct);
        if (wallet.IsError || wallet.Result is null) return AZOAResult<WalletOwnershipProof>.Failure(wallet.Message);
        if (_walletAuth is null || _ownershipProofs is null) return AZOAResult<WalletOwnershipProof>.Failure(OwnershipProofUnavailable);
        if (!WalletAuthIdentity.TryCreate(wallet.Result.Address, wallet.Result.ChainType, out var address, out var chain))
            return AZOAResult<WalletOwnershipProof>.Failure("This wallet's chain does not support signature ownership proofs.");
        var control = await _walletAuth.VerifyOwnershipProofAsync(userId, walletId, address, chain, signature, message, ct);
        if (control.IsError) return AZOAResult<WalletOwnershipProof>.Failure(control.Message);
        if (control.Result.Address != address || control.Result.ChainType != chain)
            return AZOAResult<WalletOwnershipProof>.Failure("The verified challenge is bound to a different wallet identity.");
        var recorded = await _ownershipProofs.RecordAsync(new WalletOwnershipProof(userId, walletId, chain, address, DateTime.UtcNow), ct);
        if (recorded.IsError || recorded.Result is null) return AZOAResult<WalletOwnershipProof>.Failure(recorded.Message);
        if (recorded.Result.SupersededUserId is { } previous)
            _logger?.LogWarning("Wallet ownership proof for {Chain}:{Address} moved from stale holder {PreviousUserId} to {UserId} (wallet {WalletId}).",
                chain, address, previous, userId, walletId);
        return AZOAResult<WalletOwnershipProof>.Success(recorded.Result.Proof, "Wallet ownership proven.");
    }

    private async Task<AZOAResult<IWallet>> OwnedExternalWalletAsync(Guid walletId, Guid userId, CancellationToken ct)
    {
        var wallet = await _walletStore.GetByIdAsync(walletId, ct);
        if (wallet.IsError || wallet.Result is null || wallet.Result.UserId != userId) return AZOAResult<IWallet>.Failure("Wallet not found.");
        return wallet.Result.WalletType == WalletType.External ? wallet
            : AZOAResult<IWallet>.Failure("Platform wallets are custodial; ownership proofs apply to external wallets.");
    }

    private const string ProvenElsewhere = "This address is proven by another account whose wallet still holds it; that account must remove the wallet first.";

    /// <summary>RV-L4-09: only a still-valid foreign proof blocks; a stale one is superseded by the new holder's ownership proof.</summary>
    private async Task<bool> ProvenByAnotherUserAsync(string chainType, string address, Guid userId)
        => _ownershipProofs is not null && await _ownershipProofs.IsHeldByAnotherUserAsync(chainType, address, userId);

    // ─── New: Export wallet private key ───

    public async Task<AZOAResult<WalletExportResult>> ExportWalletAsync(Guid walletId, Guid userId, AZOARequest? request = null)
    {
        var walletResult = await _walletStore.GetByIdAsync(walletId, default);
        if (walletResult.IsError || walletResult.Result == null)
            return new AZOAResult<WalletExportResult> { IsError = true, Message = "Wallet not found." };

        var wallet = walletResult.Result;

        if (wallet.UserId != userId)
            return new AZOAResult<WalletExportResult> { IsError = true, Message = "Wallet not owned by this user." };

        if (wallet.WalletType != WalletType.Platform)
            return new AZOAResult<WalletExportResult> { IsError = true, Message = "Only platform-generated wallets can be exported. External wallets are managed by their respective browser wallet." };

        if (string.IsNullOrEmpty(wallet.EncryptedPrivateKey))
            return new AZOAResult<WalletExportResult> { IsError = true, Message = "No private key stored for this wallet." };

        try
        {
            var privateKey = _keyService.DecryptPrivateKey(wallet.EncryptedPrivateKey);
            var seedPhrase = wallet.EncryptedSeedPhrase != null
                ? _keyService.DecryptSeedPhrase(wallet.EncryptedSeedPhrase)
                : null;

            return new AZOAResult<WalletExportResult>
            {
                Result = new WalletExportResult
                {
                    WalletId = wallet.Id,
                    ChainType = wallet.ChainType,
                    Address = wallet.Address,
                    PublicKey = wallet.PublicKey,
                    PrivateKey = privateKey,
                    SeedPhrase = seedPhrase
                },
                Message = "Export successful. Handle with extreme care."
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<WalletExportResult> { IsError = true, Message = $"Decryption failed: {ex.Message}" };
        }
    }

    // ─── New: Top-up a wallet via faucet (dev / test networks only) ───

    public async Task<AZOAResult<object>> TopUpAsync(Guid walletId, decimal? amount, Guid userId, AZOARequest? request = null, string? clientIdempotencyKey = null)
    {
        var walletResult = await _walletStore.GetByIdAsync(walletId, default);
        if (walletResult.IsError || walletResult.Result == null)
            return new AZOAResult<object> { IsError = true, Message = "Wallet not found." };

        var wallet = walletResult.Result;

        // Ownership check — mirror ExportWalletAsync.
        if (wallet.UserId != userId)
            return new AZOAResult<object> { IsError = true, Message = "Wallet not owned by this user." };

        // HARD GUARD: never dispense on mainnet.
        var network = _blockchainConfig.GetDefaultNetwork(wallet.ChainType);
        if (network == ChainNetwork.Mainnet)
            return new AZOAResult<object> { IsError = true, Message = "Top-up (faucet) is disabled on mainnet." };

        var defaultAmount = _config.GetValue<decimal?>("Blockchain:Faucet:DefaultAmount") ?? 5m;
        var maxAmount = _config.GetValue<decimal?>("Blockchain:Faucet:MaxAmount") ?? defaultAmount;
        var dispenseAmount = amount.GetValueOrDefault(defaultAmount);
        if (dispenseAmount <= 0)
            return new AZOAResult<object> { IsError = true, Message = "Amount must be a positive value." };
        if (dispenseAmount > maxAmount)
            return new AZOAResult<object> { IsError = true, Message = $"Top-up is capped at {maxAmount} per request." };

        // Faucet operations are provider-scoped (defined on IBlockchainProvider):
        // the chain owns whether and how it tops up. The manager stays chain-agnostic
        // — resolve the provider, check it supports a faucet, then delegate.
        IBlockchainProvider provider;
        try
        {
            provider = _chainFactory.GetProvider(wallet.ChainType, network);
        }
        catch (Exception ex)
        {
            // No provider registered for this chain ⇒ no faucet path.
            return new AZOAResult<object>
            {
                IsError = true,
                Message = $"Top-up not supported for chain {wallet.ChainType}.",
                Exception = ex
            };
        }

        if (provider is null || !provider.SupportsFaucet)
            return new AZOAResult<object>
            {
                IsError = true,
                Message = $"Top-up not supported for chain {wallet.ChainType}."
            };

        // Client-supplied Idempotency-Key (if any) wins; otherwise a server-side
        // provider derives a deterministic content key from (chain, recipient,
        // amount) — absence is still dedup-safe, no random per-request key.
        AZOAResult<FaucetDispenseResult> dispense;
        try
        {
            dispense = await provider.DispenseFromFaucetAsync(
                wallet.Address, dispenseAmount, clientIdempotencyKey, ct: default);
        }
        catch (Exception ex)
        {
            // Providers should surface failures as an error result; guard the
            // boundary so a throwing provider never escapes the manager.
            return new AZOAResult<object>
            {
                IsError = true,
                Message = $"{wallet.ChainType} faucet failed: {ex.Message}",
                Exception = ex
            };
        }

        if (dispense.IsError || dispense.Result is null)
            return new AZOAResult<object>
            {
                IsError = true,
                Message = dispense.Message,
                Exception = dispense.Exception
            };

        return new AZOAResult<object>
        {
            Result = new
            {
                txHash = dispense.Result.TxHash,
                amount = dispenseAmount,
                chain = wallet.ChainType,
                network = network.ToString()
            },
            Message = dispense.Result.Message
        };
    }

    private async Task UnsetPreviousDefaultAsync(Guid userId, string chainType, Guid exceptWalletId)
    {
        var all = await _walletStore.GetAllAsync(default);
        var previous = all.Result?.FirstOrDefault(w =>
            w.UserId == userId &&
            w.ChainType.Equals(chainType, StringComparison.OrdinalIgnoreCase) &&
            w.IsDefault &&
            w.Id != exceptWalletId);

        if (previous != null)
        {
            previous.IsDefault = false;
            await _walletStore.UpsertAsync(previous, default);
        }
    }
}
