using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain;
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
    private readonly BlockchainConfigurationManager _blockchainConfig;

    public WalletManager(
        IWalletStore walletStore,
        IHolonStore holonStore,
        IBlockchainProviderFactory chainFactory,
        WalletKeyService keyService,
        IConfiguration config)
    {
        _walletStore = walletStore;
        _holonStore = holonStore;
        _chainFactory = chainFactory;
        _keyService = keyService;
        _config = config;
        _blockchainConfig = new BlockchainConfigurationManager(config);
    }

    public async Task<AZOAResult<IWallet>> GetAsync(Guid id, Guid avatarId, AZOARequest? request = null)
    {
        var result = await _walletStore.GetByIdAsync(id, default);
        if (result.IsError || result.Result == null) return result;

        if (result.Result.AvatarId != avatarId)
            return new AZOAResult<IWallet> { IsError = true, Message = "Wallet not found." };

        return result;
    }

    public async Task<AZOAResult<IEnumerable<IWallet>>> QueryAsync(WalletQueryRequest query, Guid avatarId, AZOARequest? request = null)
    {
        var all = await _walletStore.GetAllAsync(default);
        if (all.IsError || all.Result == null) return all;

        // Force the filter to the authenticated avatar — never trust query.AvatarId.
        var filtered = all.Result.Where(w => w.AvatarId == avatarId);

        if (!string.IsNullOrEmpty(query.ChainType))
            filtered = filtered.Where(w => w.ChainType.Equals(query.ChainType, StringComparison.OrdinalIgnoreCase));
        if (query.IsDefault.HasValue)
            filtered = filtered.Where(w => w.IsDefault == query.IsDefault.Value);

        return new AZOAResult<IEnumerable<IWallet>> { Result = filtered.ToList(), Message = "Success" };
    }

    public async Task<AZOAResult<IWallet>> CreateAsync(WalletCreateModel model, Guid avatarId, AZOARequest? request = null)
    {
        // Address uniqueness per chain
        var all = await _walletStore.GetAllAsync(default);
        var existing = all.Result?.FirstOrDefault(w =>
            w.Address.Equals(model.Address, StringComparison.OrdinalIgnoreCase) &&
            w.ChainType.Equals(model.ChainType, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
            return new AZOAResult<IWallet> { IsError = true, Message = "Wallet address already exists for this chain." };

        var wallet = new Wallet
        {
            AvatarId = avatarId,
            ChainType = model.ChainType,
            Address = model.Address,
            PublicKey = model.PublicKey,
            Label = model.Label,
            IsDefault = model.IsDefault,
            WalletType = model.WalletType
        };

        if (model.IsDefault)
        {
            await UnsetPreviousDefaultAsync(avatarId, model.ChainType, wallet.Id);
        }

        return await _walletStore.UpsertAsync(wallet, default);
    }

    public async Task<AZOAResult<IWallet>> UpdateAsync(Guid id, WalletUpdateModel model, Guid avatarId, AZOARequest? request = null)
    {
        var existing = await _walletStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null) return existing;

        if (existing.Result.AvatarId != avatarId)
            return new AZOAResult<IWallet> { IsError = true, Message = "Wallet not found." };

        var wallet = (Wallet)existing.Result;
        if (model.Label != null) wallet.Label = model.Label;

        if (model.IsDefault.HasValue && model.IsDefault.Value && !wallet.IsDefault)
        {
            await UnsetPreviousDefaultAsync(wallet.AvatarId, wallet.ChainType, wallet.Id);
            wallet.IsDefault = true;
        }
        else if (model.IsDefault.HasValue)
        {
            wallet.IsDefault = model.IsDefault.Value;
        }

        return await _walletStore.UpsertAsync(wallet, default);
    }

    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid avatarId, AZOARequest? request = null)
    {
        var existing = await _walletStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = "Wallet not found." };

        if (existing.Result.AvatarId != avatarId)
            return new AZOAResult<bool> { IsError = true, Message = "Wallet not found." };

        return await _walletStore.DeleteAsync(id, default);
    }

    public async Task<AZOAResult<bool>> SetDefaultAsync(Guid avatarId, Guid walletId, AZOARequest? request = null)
    {
        var walletResult = await _walletStore.GetByIdAsync(walletId, default);
        if (walletResult.IsError || walletResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = "Wallet not found." };

        var wallet = walletResult.Result;
        if (wallet.AvatarId != avatarId)
            return new AZOAResult<bool> { IsError = true, Message = "Wallet not owned by avatar." };

        await UnsetPreviousDefaultAsync(avatarId, wallet.ChainType, walletId);

        wallet.IsDefault = true;
        var saveResult = await _walletStore.UpsertAsync(wallet, default);
        if (saveResult.IsError)
            return new AZOAResult<bool> { IsError = true, Message = saveResult.Message };

        return new AZOAResult<bool> { Result = true, Message = "Default wallet set." };
    }

    public async Task<AZOAResult<PortfolioResult>> GetPortfolioAsync(Guid walletId, Guid avatarId, AZOARequest? request = null)
    {
        var walletResult = await _walletStore.GetByIdAsync(walletId, default);
        if (walletResult.IsError || walletResult.Result == null)
            return new AZOAResult<PortfolioResult> { IsError = true, Message = "Wallet not found." };

        var wallet = walletResult.Result;

        if (wallet.AvatarId != avatarId)
            return new AZOAResult<PortfolioResult> { IsError = true, Message = "Wallet not found." };

        // Stub: linked NFT Holons for this avatar
        var allHolons = await _holonStore.QueryAsync(null, default);
        var nfts = allHolons.Result?
            .Where(h => h.AvatarId == wallet.AvatarId && h.AssetType == "NFT")
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

        var nativeDecimals = BlockchainAmounts.NativeDecimalsFor(chainType);
        assets.Add(new PortfolioAsset
        {
            Id = symbol,
            Kind = PortfolioAssetKind.Native,
            Symbol = symbol,
            Name = chainType,
            Decimals = nativeDecimals,
            // The provider balance is already in whole-coin (display) units; derive the
            // base-unit raw amount from the chain's native decimals.
            RawAmount = BlockchainAmounts.ToBaseUnits(nativeDisplayBalance, nativeDecimals),
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
                DisplayAmount = BlockchainAmounts.FromBaseUnits(fungible.RawAmount, fungible.Decimals),
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

    public async Task<AZOAResult<IWallet>> GenerateWalletAsync(WalletGenerateRequest model, Guid avatarId, AZOARequest? request = null)
    {
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
                AvatarId = avatarId,
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
                await UnsetPreviousDefaultAsync(avatarId, model.ChainType, wallet.Id);

            return await _walletStore.UpsertAsync(wallet, default);
        }
        catch (NotSupportedException ex)
        {
            return new AZOAResult<IWallet> { IsError = true, Message = ex.Message };
        }
    }

    // ─── New: Connect an external wallet (MetaMask, Ghost, etc.) ───

    public async Task<AZOAResult<IWallet>> ConnectWalletAsync(WalletConnectRequest model, Guid avatarId, AZOARequest? request = null)
    {
        if (string.IsNullOrWhiteSpace(model.Address))
            return new AZOAResult<IWallet> { IsError = true, Message = "Address is required." };

        // Optional: Verify ownership via signed message
        if (!string.IsNullOrEmpty(model.SignedMessage) && !string.IsNullOrEmpty(model.OriginalMessage))
        {
            // In production, verify the signature using chain-specific recovery
            // For now, trust the address if they provide it (lightweight verification)
        }

        // Check uniqueness
        var all = await _walletStore.GetAllAsync(default);
        var existing = all.Result?.FirstOrDefault(w =>
            w.Address.Equals(model.Address, StringComparison.OrdinalIgnoreCase) &&
            w.ChainType.Equals(model.ChainType, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            // If the wallet belongs to this avatar, return it
            if (existing.AvatarId == avatarId)
                return new AZOAResult<IWallet> { Result = existing, Message = "Wallet already connected." };

            return new AZOAResult<IWallet> { IsError = true, Message = "Address already registered by another avatar." };
        }

        var wallet = new Wallet
        {
            AvatarId = avatarId,
            ChainType = model.ChainType,
            Address = model.Address,
            PublicKey = model.PublicKey,
            Label = model.Label,
            IsDefault = model.IsDefault,
            WalletType = WalletType.External
        };

        if (model.IsDefault)
            await UnsetPreviousDefaultAsync(avatarId, model.ChainType, wallet.Id);

        return await _walletStore.UpsertAsync(wallet, default);
    }

    // ─── New: Export wallet private key ───

    public async Task<AZOAResult<WalletExportResult>> ExportWalletAsync(Guid walletId, Guid avatarId, AZOARequest? request = null)
    {
        var walletResult = await _walletStore.GetByIdAsync(walletId, default);
        if (walletResult.IsError || walletResult.Result == null)
            return new AZOAResult<WalletExportResult> { IsError = true, Message = "Wallet not found." };

        var wallet = walletResult.Result;

        if (wallet.AvatarId != avatarId)
            return new AZOAResult<WalletExportResult> { IsError = true, Message = "Wallet not owned by this avatar." };

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

    public async Task<AZOAResult<object>> TopUpAsync(Guid walletId, decimal? amount, Guid avatarId, AZOARequest? request = null, string? clientIdempotencyKey = null)
    {
        var walletResult = await _walletStore.GetByIdAsync(walletId, default);
        if (walletResult.IsError || walletResult.Result == null)
            return new AZOAResult<object> { IsError = true, Message = "Wallet not found." };

        var wallet = walletResult.Result;

        // Ownership check — mirror ExportWalletAsync.
        if (wallet.AvatarId != avatarId)
            return new AZOAResult<object> { IsError = true, Message = "Wallet not owned by this avatar." };

        // HARD GUARD: never dispense on mainnet.
        var network = _blockchainConfig.GetDefaultNetwork(wallet.ChainType);
        if (network == ChainNetwork.Mainnet)
            return new AZOAResult<object> { IsError = true, Message = "Top-up (faucet) is disabled on mainnet." };

        var defaultAmount = _config.GetValue<decimal?>("Blockchain:Faucet:DefaultAmount") ?? 5m;
        var dispenseAmount = amount.GetValueOrDefault(defaultAmount);
        if (dispenseAmount <= 0)
            return new AZOAResult<object> { IsError = true, Message = "Amount must be a positive value." };

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

    private async Task UnsetPreviousDefaultAsync(Guid avatarId, string chainType, Guid exceptWalletId)
    {
        var all = await _walletStore.GetAllAsync(default);
        var previous = all.Result?.FirstOrDefault(w =>
            w.AvatarId == avatarId &&
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
