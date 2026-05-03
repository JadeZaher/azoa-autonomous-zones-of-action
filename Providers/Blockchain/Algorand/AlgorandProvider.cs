using Algorand.Algod;
using Algorand.Algod.Model;
using Algorand.Algod.V2;
using Algorand.Indexer;
using Algorand.Indexer.Model;
using Algorand.V2;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models.Responses;
using Account = Algorand.V2.Account;
using Transaction = Algorand.V2.Transaction;

namespace OASIS.WebAPI.Providers.Blockchain.Algorand;

/// <summary>
/// Real Algorand devnet provider implementation with comprehensive functionality
/// </summary>
public class AlgorandProvider : BaseBlockchainProvider, IAlgorandASAModule
{
    private readonly AlgodClient _algodClient;
    private readonly IndexerClient? _indexerClient;
    private readonly AlgorandTransactionBuilder _transactionBuilder;
    private readonly BlockchainConfigurationManager _configManager;
    
    public string ChainType => "Algorand";
    public string CapabilityName => "Algorand.ASA";
    
    public AlgorandProvider(IConfiguration config) : base(config)
    {
        _configManager = new BlockchainConfigurationManager(config);
        
        // Get current configuration
        var network = _configManager.GetDefaultNetworkForChain(ChainType);
        var chainConfig = _configManager.GetChainConfiguration(ChainType, network);
        
        // Initialize Algod client
        _algodClient = CreateAlgodClient(chainConfig);
        
        // Initialize Indexer client if available
        _indexerClient = chainConfig.IndexerUrl != null ? CreateIndexerClient(chainConfig) : null;
        
        // Initialize transaction builder
        _transactionBuilder = new AlgorandTransactionBuilder(_algodClient);
        
        ActiveNetwork = network;
    }
    
    private AlgodClient CreateAlgodClient(BlockchainChainConfig config)
    {
        var httpClient = new HttpClient { BaseAddress = new Uri(config.NodeUrl) };
        
        if (!string.IsNullOrEmpty(config.ApiToken))
        {
            httpClient.DefaultRequestHeaders.Add("X-Algo-API-Token", config.ApiToken);
        }
        
        httpClient.Timeout = TimeSpan.FromMilliseconds(config.TimeoutMs);
        
        return new AlgodClient(httpClient, config.NodeUrl);
    }
    
    private IndexerClient? CreateIndexerClient(BlockchainChainConfig config)
    {
        if (string.IsNullOrEmpty(config.IndexerUrl))
            return null;
            
        var httpClient = new HttpClient { BaseAddress = new Uri(config.IndexerUrl) };
        
        if (!string.IsNullOrEmpty(config.ApiToken))
        {
            httpClient.DefaultRequestHeaders.Add("X-Algo-API-Token", config.ApiToken);
        }
        
        httpClient.Timeout = TimeSpan.FromMilliseconds(config.TimeoutMs);
        
        return new IndexerClient(httpClient, config.IndexerUrl);
    }
    
    public override async Task<OASISResult<string>> GetBalanceAsync(string address, string? tokenId = null, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(address))
                return CreateErrorResponse<string>("Address is required");
            
            // Validate address format first
            var validation = await ValidateAddressFormatAsync(address);
            if (!validation.Success || !validation.Result)
                return CreateErrorResponse<string>("Invalid Algorand address format");
            
            return tokenId switch
            {
                null => await GetNativeTokenBalanceAsync(address, ct),
                _ => await GetAssetBalanceAsync(address, tokenId, ct)
            };
        }
        catch (Exception ex)
        {
            return CreateErrorResponse<string>($"Error retrieving balance: {ex.Message}", ex);
        }
    }
    
    private async Task<OASISResult<string>> GetNativeTokenBalanceAsync(string address, CancellationToken ct)
    {
        var accountInfo = await ExecuteWithRetryAsync(async () => 
            await _algodClient.AccountInformationAsync(address));
        
        var balanceAlgos = accountInfo.Amount / 1_000_000_000.0; // Convert from microAlgos to ALGO
        var balance = balanceAlgos.ToString("F6");
        
        return new OASISResult<string>
        {
            Result = balance,
            Message = $"Retrieved ALGO balance: {balance} for address {address}"
        };
    }
    
    private async Task<OASISResult<string>> GetAssetBalanceAsync(string address, string tokenId, CancellationToken ct)
    {
        if (_indexerClient == null)
            return CreateErrorResponse<string>("Indexer client not available for ASA queries");
        
        var assets = await ExecuteWithRetryAsync(async () => 
            await _indexerClient.LookupAccountAssetsAsync(address));
        
        if (!assets.Assets.Any(a => a.Id.ToString() == tokenId))
            return new OASISResult<string> { Result = "0", Message = "Asset not found in account" };
        
        var asset = assets.Assets.First(a => a.Id.ToString() == tokenId);
        return new OASISResult<string>
        {
            Result = asset.Amount.ToString(),
            Message = $"Retrieved ASA balance: {asset.Amount} for asset {tokenId}"
        };
    }
    
    public override async Task<OASISResult<bool>> ValidateAddressAsync(string address, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(address))
                return new OASISResult<bool> { Success = false, Message = "Address is required" };
            
            // First validate format
            var formatValidation = await ValidateAddressFormatAsync(address);
            if (!formatValidation.Success || !formatValidation.Result)
                return formatValidation;
            
            // Then validate address exists by checking account info
            try
            {
                await ExecuteWithRetryAsync(async () => 
                    await _algodClient.AccountInformationAsync(address));
                
                return new OASISResult<bool>
                {
                    Result = true,
                    Message = "Valid Algorand address and exists on network"
                };
            }
            catch
            {
                return new OASISResult<bool>
                {
                    Result = false,
                    Success = false,
                    Message = "Address format is valid but account not found on network"
                };
            }
        }
        catch (Exception ex)
        {
            return new OASISResult<bool>
            {
                Result = false,
                Success = false,
                Error = ex.Message,
                Message = "Address validation failed"
            };
        }
    }
    
    protected override async Task<OASISResult<bool>> ValidateAddressFormatAsync(string address)
    {
        // Algorand addresses are base32 encoded and should be 58 characters
        if (address.Length != 58)
            return new OASISResult<bool> { Result = false, Message = "Address must be 58 characters" };
        
        // Check if all characters are valid base32 characters
        var validChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        if (!address.All(c => validChars.Contains(c)))
            return new OASISResult<bool> { Result = false, Message = "Address contains invalid characters" };
        
        return new OASISResult<bool> { Result = true, Message = "Valid Algorand address format" };
    }
    
    public override async Task<OASISResult<string>> TransferAsync(
        string tokenId, 
        string fromAddress, 
        string toAddress, 
        int amount, 
        CancellationToken ct = default)
    {
        try
        {
            // Validate inputs
            if (string.IsNullOrWhiteSpace(fromAddress) || string.IsNullOrWhiteSpace(toAddress))
                return CreateErrorResponse<string>("From and to addresses are required");
            
            if (amount <= 0)
                return CreateErrorResponse<string>("Amount must be positive");
            
            var fromValidation = await ValidateAddressAsync(fromAddress);
            var toValidation = await ValidateAddressAsync(toAddress);
            
            if (!fromValidation.Success) return CreateErrorResponse<string>(fromValidation.Message);
            if (!toValidation.Success) return CreateErrorResponse<string>(toValidation.Message);
            
            // Get account info for signing
            var accountInfo = await ExecuteWithRetryAsync(async () => 
                await _algodClient.AccountInformationAsync(fromAddress));
            
            return tokenId switch
            {
                null or "0" => await TransferNativeTokenAsync(accountInfo, toAddress, (ulong)amount, ct),
                _ => await TransferAssetAsync(accountInfo, tokenId, toAddress, (ulong)amount, ct)
            };
        }
        catch (Exception ex)
        {
            return CreateErrorResponse<string>($"Error transferring: {ex.Message}", ex);
        }
    }
    
    private async Task<OASISResult<string>> TransferNativeTokenAsync(
        Account accountInfo, 
        string toAddress, 
        ulong amount, 
        CancellationToken ct)
    {
        var txn = await _transactionBuilder.BuildPaymentTransactionAsync(
            accountInfo.Address.ToString(),
            toAddress,
            amount * 1_000_000_000, // Convert ALGO to microAlgos
            ct);
        
        var signedTxn = txn.Sign(accountInfo.PrivateKey);
        var txId = await ExecuteWithRetryAsync(async () => 
            await _algodClient.SendRawTransactionAsync(signedTxn));
        
        return new OASISResult<string>
        {
            Result = txId,
            Message = $"Transferred {amount / 1_000_000_000.0} ALGO to {toAddress}"
        };
    }
    
    private async Task<OASISResult<string>> TransferAssetAsync(
        Account accountInfo, 
        string tokenId, 
        string toAddress, 
        ulong amount, 
        CancellationToken ct)
    {
        var txn = await _transactionBuilder.BuildAssetTransferTransactionAsync(
            accountInfo.Address.ToString(),
            toAddress,
            ulong.Parse(tokenId),
            amount,
            ct);
        
        var signedTxn = txn.Sign(accountInfo.PrivateKey);
        var txId = await ExecuteWithRetryAsync(async () => 
            await _algodClient.SendRawTransactionAsync(signedTxn));
        
        return new OASISResult<string>
        {
            Result = txId,
            Message = $"Transferred {amount} of asset {tokenId} to {toAddress}"
        };
    }
    
    public override async Task<OASISResult<string>> MintAsync(
        string tokenUri, 
        int amount, 
        string assetType, 
        string walletAddress, 
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(walletAddress))
                return CreateErrorResponse<string>("Wallet address is required");
            
            if (amount <= 0)
                return CreateErrorResponse<string>("Amount must be positive");
            
            var validation = await ValidateAddressAsync(walletAddress);
            if (!validation.Success) return CreateErrorResponse<string>(validation.Message);
            
            // Use CreateASA for ASA creation
            return await CreateASAAsync(
                name: assetType,
                unitName: assetType.ToUpper().Substring(0, Math.Min(8, assetType.Length)),
                total: amount,
                decimals: 0,
                managerAddress: walletAddress,
                reserveAddress: walletAddress,
                freezeAddress: walletAddress,
                clawbackAddress: walletAddress,
                walletAddress: walletAddress,
                ct);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse<string>($"Error minting asset: {ex.Message}", ex);
        }
    }
    
    public override async Task<OASISResult<string>> BurnAsync(
        string tokenId, 
        int amount, 
        string walletAddress, 
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tokenId) || string.IsNullOrWhiteSpace(walletAddress))
                return CreateErrorResponse<string>("Token ID and wallet address are required");
            
            if (amount <= 0)
                return CreateErrorResponse<string>("Amount must be positive");
            
            var validation = await ValidateAddressAsync(walletAddress);
            if (!validation.Success) return CreateErrorResponse<string>(validation.Message);
            
            var accountInfo = await ExecuteWithRetryAsync(async () => 
                await _algodClient.AccountInformationAsync(walletAddress));
            
            var txn = await _transactionBuilder.BuildAssetTransferTransactionAsync(
                walletAddress,
                walletAddress, // Send back to self to burn
                ulong.Parse(tokenId),
                (ulong)amount,
                ct);
            
            var signedTxn = txn.Sign(accountInfo.PrivateKey);
            var txId = await ExecuteWithRetryAsync(async () => 
                await _algodClient.SendRawTransactionAsync(signedTxn));
            
            return new OASISResult<string>
            {
                Result = txId,
                Message = $"Burned {amount} of asset {tokenId}"
            };
        }
        catch (Exception ex)
        {
            return CreateErrorResponse<string>($"Error burning asset: {ex.Message}", ex);
        }
    }
    
    public override async Task<OASISResult<string>> GetTransactionStatusAsync(
        string txHash, 
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(txHash))
                return CreateErrorResponse<string>("Transaction hash is required");
            
            var transaction = await ExecuteWithRetryAsync(async () => 
                await _algodClient.TransactionInformationAsync(txHash));
            
            var status = new Dictionary<string, object>
            {
                ["txHash"] = txHash,
                ["status"] = transaction.Confirmed != null ? "confirmed" : "pending",
                ["chain"] = "Algorand",
                ["confirmedAt"] = transaction.Confirmed?.ToString(),
                ["round"] = transaction.Confirmed?.Round?.ToString(),
                ["fee"] = transaction.Fee.ToString(),
                ["sender"] = transaction.Sender.ToString(),
                ["receiver"] = transaction.PaymentTransaction?.Receiver?.ToString() ?? "",
                ["amount"] = transaction.PaymentTransaction?.Amount?.ToString() ?? "0",
                ["assetAmount"] = transaction.AssetTransferTransaction?.AssetAmount?.ToString() ?? "0",
                ["assetId"] = transaction.AssetTransferTransaction?.AssetId?.ToString() ?? "0"
            };
            
            return new OASISResult<Dictionary<string, object>>
            {
                Result = status,
                Message = "Transaction status retrieved successfully"
            } as OASISResult<string> ?? throw new InvalidOperationException("Unexpected response type");
        }
        catch (Exception ex)
        {
            return CreateErrorResponse<string>($"Error getting transaction status: {ex.Message}", ex);
        }
    }
    
    public override async Task<OASISResult<Dictionary<string, object>>> GetTokenMetadataAsync(
        string tokenId, 
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tokenId))
                return CreateErrorResponse<Dictionary<string, object>>("Token ID is required");
            
            if (_indexerClient == null)
                return CreateErrorResponse<Dictionary<string, object>>("Indexer client not available");
            
            var asset = await ExecuteWithRetryAsync(async () => 
                await _indexerClient.LookupAssetByIdAsync(long.Parse(tokenId)));
            
            var metadata = new Dictionary<string, object>
            {
                ["chain"] = "Algorand",
                ["assetId"] = tokenId,
                ["name"] = asset.Asset.Params.Name ?? "Unknown",
                ["unitName"] = asset.Asset.Params.UnitName ?? "",
                ["totalSupply"] = asset.Asset.Params.Total.ToString(),
                ["decimals"] = asset.Asset.Params.Decimals.ToString(),
                ["creator"] = asset.Asset.Params.Creator.ToString(),
                ["fetchedAt"] = DateTime.UtcNow,
                ["isFrozen"] = asset.Asset.Params.IsFrozen,
                ["url"] = asset.Asset.Params.Url ?? ""
            };
            
            return new OASISResult<Dictionary<string, object>>
            {
                Result = metadata,
                Message = "Metadata retrieved successfully"
            };
        }
        catch (Exception ex)
        {
            return CreateErrorResponse<Dictionary<string, object>>($"Error fetching metadata: {ex.Message}", ex);
        }
    }
    
    public override async Task<OASISResult<List<Dictionary<string, object>>>> GetTokensByOwnerAsync(
        string ownerAddress, 
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ownerAddress))
                return CreateErrorResponse<List<Dictionary<string, object>>>("Owner address is required");
            
            var validation = await ValidateAddressAsync(ownerAddress);
            if (!validation.Success) return CreateErrorResponse<List<Dictionary<string, object>>>(validation.Message);
            
            if (_indexerClient == null)
                return CreateErrorResponse<List<Dictionary<string, object>>>("Indexer client not available");
            
            var assets = await ExecuteWithRetryAsync(async () => 
                await _indexerClient.LookupAccountAssetsAsync(ownerAddress));
            
            var tokens = new List<Dictionary<string, object>>();
            
            foreach (var asset in assets.Assets)
            {
                var tokenInfo = new Dictionary<string, object>
                {
                    ["assetId"] = asset.Id.ToString(),
                    ["amount"] = asset.Amount.ToString(),
                    ["creator"] = asset.Creator.ToString(),
                    ["fetchedAt"] = DateTime.UtcNow
                };
                
                // Try to get detailed metadata
                try
                {
                    var detailedAsset = await ExecuteWithRetryAsync(async () => 
                        await _indexerClient.LookupAssetByIdAsync(asset.Id));
                    
                    tokenInfo["name"] = detailedAsset.Asset.Params.Name ?? "Unknown";
                    tokenInfo["unitName"] = detailedAsset.Asset.Params.UnitName ?? "";
                    tokenInfo["totalSupply"] = detailedAsset.Asset.Params.Total.ToString();
                    tokenInfo["decimals"] = detailedAsset.Asset.Params.Decimals.ToString();
                    tokenInfo["isFrozen"] = detailedAsset.Asset.Params.IsFrozen;
                    tokenInfo["url"] = detailedAsset.Asset.Params.Url ?? "";
                }
                catch
                {
                    // Keep basic info if detailed fetch fails
                }
                
                tokens.Add(tokenInfo);
            }
            
            return new OASISResult<List<Dictionary<string, object>>>
            {
                Result = tokens,
                Message = $"Retrieved {tokens.Count} tokens"
            };
        }
        catch (Exception ex)
        {
            return CreateErrorResponse<List<Dictionary<string, object>>>($"Error fetching tokens: {ex.Message}", ex);
        }
    }
    
    protected override async Task<OASISResult<Dictionary<string, object>>> GetChainInfoInternalAsync()
    {
        try
        {
            var status = await ExecuteWithRetryAsync(async () => 
                await _algodClient.GetStatusAsync());
            
            var genesisHash = await ExecuteWithRetryAsync(async () => 
                await _algodClient.GetGenesisHashAsync());
            
            var info = new Dictionary<string, object>
            {
                ["chain"] = "Algorand",
                ["network"] = ActiveNetwork.ToString(),
                ["nodeVersion"] = status.LastVersion.ToString(),
                ["genesisHash"] = genesisHash,
                ["round"] = status.LastRound.ToString(),
                ["time"] = DateTime.UtcNow,
                ["apiVersion"] = "v2"
            };
            
            return new OASISResult<Dictionary<string, object>>
            {
                Result = info,
                Message = "Chain info retrieved successfully"
            };
        }
        catch (Exception ex)
        {
            return CreateErrorResponse<Dictionary<string, object>>($"Error fetching chain info: {ex.Message}", ex);
        }
    }
    
    // IAlgorandASAModule implementation
    public async Task<OASISResult<string>> CreateASAAsync(
        string name, 
        string unitName, 
        int total,
        int decimals,
        string managerAddress, 
        string reserveAddress, 
        string freezeAddress, 
        string clawbackAddress,
        string walletAddress, 
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(walletAddress))
                return CreateErrorResponse<string>("Wallet address is required");
            
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(unitName))
                return CreateErrorResponse<string>("Name and unit name are required");
            
            if (total <= 0)
                return CreateErrorResponse<string>("Total supply must be positive");
            
            var validation = await ValidateAddressAsync(walletAddress);
            if (!validation.Success) return CreateErrorResponse<string>(validation.Message);
            
            var accountInfo = await ExecuteWithRetryAsync(async () => 
                await _algodClient.AccountInformationAsync(walletAddress));
            
            var txn = await _transactionBuilder.BuildAssetCreationTransactionAsync(
                walletAddress,
                name,
                unitName,
                (ulong)total,
                decimals,
                managerAddress,
                reserveAddress,
                freezeAddress,
                clawbackAddress,
                ct);
            
            var signedTxn = txn.Sign(accountInfo.PrivateKey);
            var txId = await ExecuteWithRetryAsync(async () => 
                await _algodClient.SendRawTransactionAsync(signedTxn));
            
            return new OASISResult<string>
            {
                Result = txId,
                Message = $"Created ASA {name} with transaction ID {txId}"
            };
        }
        catch (Exception ex)
        {
            return CreateErrorResponse<string>($"Error creating ASA: {ex.Message}", ex);
        }
    }
    
    public async Task<OASISResult<bool>> OptInAsync(string assetId, string walletAddress, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(assetId) || string.IsNullOrWhiteSpace(walletAddress))
                return CreateErrorResponse<bool>("Asset ID and wallet address are required");
            
            var validation = await ValidateAddressAsync(walletAddress);
            if (!validation.Success) return CreateErrorResponse<bool>(validation.Message);
            
            // Check if already opted in
            if (_indexerClient == null)
                return CreateErrorResponse<bool>("Indexer client not available for asset queries");
            
            var assets = await ExecuteWithRetryAsync(async () => 
                await _indexerClient.LookupAccountAssetsAsync(walletAddress));
            
            if (assets.Assets.Any(a => a.Id.ToString() == assetId))
                return new OASISResult<bool> { Result = true, Message = "Already opted in to asset" };
            
            var accountInfo = await ExecuteWithRetryAsync(async () => 
                await _algodClient.AccountInformationAsync(walletAddress));
            
            var txn = await _transactionBuilder.BuildAssetOptInTransactionAsync(
                walletAddress,
                ulong.Parse(assetId),
                ct);
            
            var signedTxn = txn.Sign(accountInfo.PrivateKey);
            var txId = await ExecuteWithRetryAsync(async () => 
                await _algodClient.SendRawTransactionAsync(signedTxn));
            
            return new OASISResult<bool>
            {
                Result = true,
                Message = $"Successfully opted in to asset {assetId}"
            };
        }
        catch (Exception ex)
        {
            return new OASISResult<bool>
            {
                Result = false,
                Success = false,
                Error = ex.Message,
                Message = $"Error opting in to asset: {ex.Message}"
            };
        }
    }
    
    public async Task<OASISResult<string>> GetAssetHoldingAsync(string assetId, string address, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(assetId) || string.IsNullOrWhiteSpace(address))
                return CreateErrorResponse<string>("Asset ID and address are required");
            
            var validation = await ValidateAddressAsync(address);
            if (!validation.Success) return CreateErrorResponse<string>(validation.Message);
            
            if (_indexerClient == null)
                return CreateErrorResponse<string>("Indexer client not available for asset queries");
            
            var assets = await ExecuteWithRetryAsync(async () => 
                await _indexerClient.LookupAccountAssetsAsync(address));
            
            var asset = assets.Assets.FirstOrDefault(a => a.Id.ToString() == assetId);
            
            if (asset == null)
                return new OASISResult<string> { Result = "0", Message = "Asset not found in account" };
            
            return new OASISResult<string>
            {
                Result = asset.Amount.ToString(),
                Message = $"Retrieved asset holding: {asset.Amount} for asset {assetId}"
            };
        }
        catch (Exception ex)
        {
            return CreateErrorResponse<string>($"Error retrieving asset holding: {ex.Message}", ex);
        }
    }
} 
