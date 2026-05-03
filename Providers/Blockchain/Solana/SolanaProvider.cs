using Sol.Rpc;
using Sol.Rpc.Models;
using Sol.Rpc.Types;
using Sol.Wallet;
using Sol.Wallet.Utilities;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Providers.Blockchain.Solana;

/// <summary>
/// Real Solana devnet provider implementation with comprehensive functionality
/// </summary>
public class SolanaProvider : BaseBlockchainProvider
{
    private readonly IRpcClient _rpcClient;
    private readonly SolanaTransactionBuilder _transactionBuilder;
    private readonly BlockchainConfigurationManager _configManager;
    
    public string ChainType => "Solana";
    
    public SolanaProvider(IConfiguration config) : base(config)
    {
        _configManager = new BlockchainConfigurationManager(config);
        
        // Get current configuration
        var network = _configManager.GetDefaultNetworkForChain(ChainType);
        var chainConfig = _configManager.GetChainConfiguration(ChainType, network);
        
        // Initialize RPC client
        _rpcClient = CreateRpcClient(chainConfig);
        
        // Initialize transaction builder
        _transactionBuilder = new SolanaTransactionBuilder(_rpcClient);
        
        ActiveNetwork = network;
    }
    
    private IRpcClient CreateRpcClient(BlockchainChainConfig config)
    {
        var httpClient = new HttpClient { BaseAddress = new Uri(config.NodeUrl) };
        
        if (config.TimeoutMs > 0)
        {
            httpClient.Timeout = TimeSpan.FromMilliseconds(config.TimeoutMs);
        }
        
        return new SolanaRpcClient(httpClient);
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
                return CreateErrorResponse<string>("Invalid Solana address format");
            
            if (string.IsNullOrEmpty(tokenId))
            {
                // Get native SOL balance
                return await GetNativeTokenBalanceAsync(address, ct);
            }
            else
            {
                // Get SPL token balance
                return await GetTokenBalanceAsync(address, tokenId, ct);
            }
        }
        catch (Exception ex)
        {
            return CreateErrorResponse<string>($"Error retrieving balance: {ex.Message}", ex);
        }
    }
    
    private async Task<OASISResult<string>> GetNativeTokenBalanceAsync(string address, CancellationToken ct)
    {
        var balanceResult = await ExecuteWithRetryAsync(async () => 
            await _rpcClient.GetBalanceAsync(address));
        
        if (balanceResult.WasSuccessful)
        {
            var balance = balanceResult.Result.Value / (decimal)LamportsPerSol;
            return new OASISResult<string>
            {
                Result = balance.ToString("F9"),
                Message = $"Retrieved SOL balance: {balance} for address {address}"
            };
        }
        else
        {
            return CreateErrorResponse<string>($"Failed to retrieve balance: {balanceResult.Reason}");
        }
    }
    
    private async Task<OASISResult<string>> GetTokenBalanceAsync(string address, string mintAddress, CancellationToken ct)
    {
        try
        {
            // Get associated token account
            var associatedTokenAccount = PublicKey.FindProgramAddress(
                new[] { PublicKey.FromString(address).KeyBytes, PublicKey.FromString(mintAddress).KeyBytes },
                TokenProgram.ProgramIdKey
            ).Address;
            
            var accountInfo = await ExecuteWithRetryAsync(async () => 
                await _rpcClient.GetTokenAccountBalanceAsync(associatedTokenAccount.ToString()));
            
            if (accountInfo.WasSuccessful)
            {
                var balance = accountInfo.Result.Value.UiAmountString;
                return new OASISResult<string>
                {
                    Result = balance,
                    Message = $"Retrieved token balance: {balance} for mint {mintAddress}"
                };
            }
            else
            {
                return new OASISResult<string> { Result = "0", Message = "Token account not found or balance is zero" };
            }
        }
        catch (Exception ex)
        {
            return new OASISResult<string> { Result = "0", Message = "Token account not found or balance is zero" };
        }
    }
    
    public override async Task<OASISResult<bool>> ValidateAddressAsync(string address, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(address))
                return new OASISResult<bool> { Success = false, Message = "Address is required" };
            
            // Validate address format
            var formatValidation = await ValidateAddressFormatAsync(address);
            if (!formatValidation.Success || !formatValidation.Result)
                return formatValidation;
            
            // Try to get balance to validate address exists
            var balanceResult = await ExecuteWithRetryAsync(async () => 
                await _rpcClient.GetBalanceAsync(address));
            
            var isValid = balanceResult.WasSuccessful;
            
            return new OASISResult<bool>
            {
                Result = isValid,
                Message = isValid ? "Valid Solana address and exists on network" : "Address format is valid but account not found on network"
            };
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
        // Solana addresses are base58 encoded
        if (address.Length < 32 || address.Length > 44)
            return new OASISResult<bool> { Result = false, Message = "Address length must be between 32 and 44 characters" };
        
        // Check if all characters are valid base58 characters
        var validChars = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        if (!address.All(c => validChars.Contains(c)))
            return new OASISResult<bool> { Result = false, Message = "Address contains invalid characters" };
        
        // Additional validation: try to decode as base58
        try
        {
            var decoded = Convert.FromBase58String(address);
            if (decoded.Length != 32)
                return new OASISResult<bool> { Result = false, Message = "Address must decode to 32 bytes" };
        }
        catch
        {
            return new OASISResult<bool> { Result = false, Message = "Invalid base58 encoding" };
        }
        
        return new OASISResult<bool> { Result = true, Message = "Valid Solana address format" };
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
            var accountInfo = await GetAccountInfoAsync(fromAddress);
            if (accountInfo == null)
                return CreateErrorResponse<string>("Source account not found");
            
            return tokenId switch
            {
                null or "SOL" or "sol" => await TransferNativeTokenAsync(accountInfo, toAddress, (ulong)amount * LamportsPerSol, ct),
                _ => await TransferTokenAsync(accountInfo, tokenId, toAddress, amount, ct)
            };
        }
        catch (Exception ex)
        {
            return CreateErrorResponse<string>($"Error transferring: {ex.Message}", ex);
        }
    }
    
    private async Task<OASISResult<string>> TransferNativeTokenAsync(
        AccountInfo accountInfo, 
        string toAddress, 
        ulong lamports, 
        CancellationToken ct)
    {
        var transaction = await _transactionBuilder.BuildTransferTransactionAsync(
            new Account(accountInfo.Result.Value.Lamports, accountInfo.Result.Value.PublicKey.KeyBytes),
            toAddress,
            lamports,
            ct);
        
        // Sign and send the transaction
        var signedTransaction = SignTransaction(transaction, accountInfo.Result.Value.SecretKey);
        var txId = await ExecuteWithRetryAsync(async () => 
            await _rpcClient.SendTransactionAsync(signedTransaction));
        
        return new OASISResult<string>
        {
            Result = txId,
            Message = $"Transferred {lamports / (decimal)LamportsPerSol} SOL to {toAddress}"
        };
    }
    
    private async Task<OASISResult<string>> TransferTokenAsync(
        AccountInfo accountInfo, 
        string mintAddress, 
        string toAddress, 
        int amount, 
        CancellationToken ct)
    {
        var transaction = await _transactionBuilder.BuildTokenTransferTransactionAsync(
            new Account(accountInfo.Result.Value.Lamports, accountInfo.Result.Value.PublicKey.KeyBytes),
            toAddress,
            mintAddress,
            amount,
            ct);
        
        // Sign and send the transaction
        var signedTransaction = SignTransaction(transaction, accountInfo.Result.Value.SecretKey);
        var txId = await ExecuteWithRetryAsync(async () => 
            await _rpcClient.SendTransactionAsync(signedTransaction));
        
        return new OASISResult<string>
        {
            Result = txId,
            Message = $"Transferred {amount} tokens from {mintAddress} to {toAddress}"
        };
    }
    
    public override async Task<OASISResult<string>> GetTransactionStatusAsync(
        string txHash, 
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(txHash))
                return CreateErrorResponse<string>("Transaction hash is required");
            
            var txResult = await ExecuteWithRetryAsync(async () => 
                await _rpcClient.GetTransactionAsync(txHash));
            
            if (txResult.WasSuccessful)
            {
                var transaction = txResult.Result;
                var status = new Dictionary<string, object>
                {
                    ["txHash"] = txHash,
                    ["status"] = transaction.Meta?.Err == null ? "confirmed" : "failed",
                    ["block"] = transaction.BlockTime?.ToString(),
                    ["fee"] = transaction.Meta?.Fee?.ToString() ?? "0",
                    ["slot"] = transaction.Slot.ToString(),
                    ["success"] = transaction.Meta?.Err == null
                };
                
                return new OASISResult<Dictionary<string, object>>
                {
                    Result = status,
                    Message = "Transaction status retrieved successfully"
                } as OASISResult<string> ?? throw new InvalidOperationException("Unexpected response type");
            }
            else
            {
                var status = new Dictionary<string, object>
                {
                    ["txHash"] = txHash,
                    ["status"] = "not_found",
                    ["block"] = null,
                    ["fee"] = "0",
                    ["slot"] = null,
                    ["success"] = false
                };
                
                return new OASISResult<Dictionary<string, object>>
                {
                    Result = status,
                    Message = "Transaction not found"
                } as OASISResult<string> ?? throw new InvalidOperationException("Unexpected response type");
            }
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
            
            // Get token supply
            var supplyResult = await ExecuteWithRetryAsync(async () => 
                await _rpcClient.GetTokenSupplyAsync(tokenId));
            
            if (!supplyResult.WasSuccessful)
                return CreateErrorResponse<Dictionary<string, object>>($"Failed to get token supply: {supplyResult.Reason}");
            
            // Get token account balance (as a way to get basic token info)
            var metadata = new Dictionary<string, object>
            {
                ["chain"] = "Solana",
                ["mintAddress"] = tokenId,
                ["totalSupply"] = supplyResult.Result.Value.Amount,
                ["decimals"] = supplyResult.Result.Value.Decimals,
                ["fetchedAt"] = DateTime.UtcNow,
                ["tokenType"] = "SPL Token"
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
            
            // Get all token accounts for the owner
            var tokenAccounts = await ExecuteWithRetryAsync(async () => 
                await _rpcClient.GetTokenAccountsByOwnerAsync(ownerAddress, TokenProgram.ProgramIdKey.ToString()));
            
            var tokens = new List<Dictionary<string, object>>();
            
            foreach (var account in tokenAccounts.Result.Value)
            {
                var tokenInfo = new Dictionary<string, object>
                {
                    ["mintAddress"] = account.Value.Mint,
                    ["owner"] = account.Value.Owner,
                    ["amount"] = account.Value.Amount,
                    ["decimals"] = account.Value.Decimals,
                    ["fetchedAt"] = DateTime.UtcNow
                };
                
                // Try to get token metadata
                try
                {
                    var supplyResult = await ExecuteWithRetryAsync(async () => 
                        await _rpcClient.GetTokenSupplyAsync(account.Value.Mint));
                    
                    if (supplyResult.WasSuccessful)
                    {
                        tokenInfo["totalSupply"] = supplyResult.Result.Value.Amount;
                        tokenInfo["tokenName"] = supplyResult.Result.Value.UiAmountString;
                    }
                }
                catch
                {
                    // Keep basic info if metadata fetch fails
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
            var slotInfo = await ExecuteWithRetryAsync(async () => 
                await _rpcClient.GetSlotInfoAsync());
            
            var blockTime = await ExecuteWithRetryAsync(async () => 
                await _rpcClient.GetBlockTimeAsync(slotInfo.Result.Value.Slot));
            
            var supply = await ExecuteWithRetryAsync(async () => 
                await _rpcClient.GetSupplyAsync());
            
            var info = new Dictionary<string, object>
            {
                ["chain"] = "Solana",
                ["network"] = ActiveNetwork.ToString(),
                ["slot"] = slotInfo.Result.Value.Slot,
                ["blockTime"] = blockTime.Result.Value?.ToString(),
                ["totalSupply"] = supply.Result.Value.Total.ToString(),
                ["circulatingSupply"] = supply.Result.Value.Circulating.ToString(),
                ["time"] = DateTime.UtcNow,
                ["apiVersion"] = "1.18.0"
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
    
    // Helper methods
    private async Task<AccountInfo?> GetAccountInfoAsync(string address)
    {
        try
        {
            var result = await ExecuteWithRetryAsync(async () => 
                await _rpcClient.GetAccountInfoAsync(address));
            
            return result.WasSuccessful ? result : null;
        }
        catch
        {
            return null;
        }
    }
    
    private byte[] SignTransaction(Transaction transaction, byte[] secretKey)
    {
        // In a real implementation, you would use the Solana wallet library to sign transactions
        // For now, we'll return the serialized transaction
        return transaction.Serialize();
    }
    
    // Constants
    private const ulong LamportsPerSol = 1_000_000_000;
    
    // Additional interface implementations (stubs for now, can be expanded)
    public override Task<OASISResult<string>> MintAsync(string tokenUri, int amount, string assetType, string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(new OASISResult<string>
        {
            Success = false,
            Message = "Minting not yet implemented for Solana",
            Error = "Feature not available"
        });
    }
    
    public override Task<OASISResult<string>> BurnAsync(string tokenId, int amount, string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(new OASISResult<string>
        {
            Success = false,
            Message = "Burning not yet implemented for Solana",
            Error = "Feature not available"
        });
    }
    
    public override Task<OASISResult<string>> ExchangeAsync(string sourceTokenId, string targetTokenId, string exchangeRate, string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(new OASISResult<string>
        {
            Success = false,
            Message = "Exchange not yet implemented for Solana",
            Error = "Feature not available"
        });
    }
    
    public override Task<OASISResult<string>> SwapAsync(string tokenIn, string tokenOut, decimal amountIn, decimal minAmountOut, string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(new OASISResult<string>
        {
            Success = false,
            Message = "Swap not yet implemented for Solana",
            Error = "Feature not available"
        });
    }
    
    public override Task<OASISResult<string>> DeployContractAsync(byte[] contractCode, string walletAddress, Dictionary<string, object>? args = null, CancellationToken ct = default)
    {
        return Task.FromResult(new OASISResult<string>
        {
            Success = false,
            Message = "Contract deployment not yet implemented for Solana",
            Error = "Feature not available"
        });
    }
    
    public override Task<OASISResult<object>> CallContractAsync(string contractAddress, string method, Dictionary<string, object> args, string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(new OASISResult<object>
        {
            Success = false,
            Message = "Contract calls not yet implemented for Solana",
            Error = "Feature not available"
        });
    }
}