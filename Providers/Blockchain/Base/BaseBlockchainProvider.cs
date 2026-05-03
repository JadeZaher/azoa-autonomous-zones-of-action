using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Providers.Blockchain.Base;

/// <summary>
/// Base class for blockchain providers with common functionality and error handling
/// </summary>
public abstract class BaseBlockchainProvider : IBlockchainProvider
{
    protected readonly IConfiguration _config;
    protected readonly HttpClient _httpClient;
    
    public string ChainType { get; protected set; } = string.Empty;
    public ChainNetwork ActiveNetwork { get; protected set; }
    
    protected BaseBlockchainProvider(IConfiguration config)
    {
        _config = config;
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }
    
    public void Initialize(BlockchainNetworkConfig config, ChainNetwork network)
    {
        ActiveNetwork = network;
        ConfigureHttpClient(config);
    }
    
    /// <summary>
    /// Configure HTTP client with network-specific settings
    /// </summary>
    protected virtual void ConfigureHttpClient(BlockchainNetworkConfig config)
    {
        if (!string.IsNullOrEmpty(config.NodeUrl))
        {
            _httpClient.BaseAddress = new Uri(config.NodeUrl);
        }
        
        if (!string.IsNullOrEmpty(config.ApiToken))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiToken}");
        }
        
        if (config.TimeoutMs > 0)
        {
            _httpClient.Timeout = TimeSpan.FromMilliseconds(config.TimeoutMs);
        }
    }
    
    /// <summary>
    /// Execute operation with retry logic and error handling
    /// </summary>
    protected async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation, 
        int maxRetries = 3, 
        int initialDelayMs = 1000)
    {
        int retryCount = 0;
        int delayMs = initialDelayMs;
        
        while (true)
        {
            try
            {
                return await operation();
            }
            catch (HttpRequestException ex) when (retryCount < maxRetries && IsRetryable(ex))
            {
                retryCount++;
                await Task.Delay(delayMs);
                delayMs = (int)(delayMs * 1.5); // Exponential backoff
            }
            catch (TimeoutException ex) when (retryCount < maxRetries)
            {
                retryCount++;
                await Task.Delay(delayMs);
                delayMs = (int)(delayMs * 1.5); // Exponential backoff
            }
            catch (Exception ex)
            {
                throw new BlockchainProviderException(
                    $"{ChainType} operation failed after {retryCount} retries", ex);
            }
        }
    }
    
    /// <summary>
    /// Execute operation with retry logic and error handling (void return)
    /// </summary>
    protected async Task ExecuteWithRetryAsync(
        Func<Task> operation, 
        int maxRetries = 3, 
        int initialDelayMs = 1000)
    {
        int retryCount = 0;
        int delayMs = initialDelayMs;
        
        while (true)
        {
            try
            {
                await operation();
                return;
            }
            catch (HttpRequestException ex) when (retryCount < maxRetries && IsRetryable(ex))
            {
                retryCount++;
                await Task.Delay(delayMs);
                delayMs = (int)(delayMs * 1.5); // Exponential backoff
            }
            catch (TimeoutException ex) when (retryCount < maxRetries)
            {
                retryCount++;
                await Task.Delay(delayMs);
                delayMs = (int)(delayMs * 1.5); // Exponential backoff
            }
            catch (Exception ex)
            {
                throw new BlockchainProviderException(
                    $"{ChainType} operation failed after {retryCount} retries", ex);
            }
        }
    }
    
    /// <summary>
    /// Check if an exception is retryable
    /// </summary>
    protected virtual bool IsRetryable(HttpRequestException ex)
    {
        // Retry on 5xx server errors, 429 (too many requests), and network timeouts
        return ex.StatusCode == null || 
               (int)ex.StatusCode >= 500 || 
               (int)ex.StatusCode == 429;
    }
    
    /// <summary>
    /// Create a standardized error response
    /// </summary>
    protected OASISResult<T> CreateErrorResponse<T>(string message, Exception? ex = null)
    {
        return new OASISResult<T>
        {
            Success = false,
            Message = message,
            Error = ex?.Message ?? "Unknown error"
        };
    }
    
    /// <summary>
    /// Validate address format for the specific blockchain
    /// </summary>
    protected abstract Task<OASISResult<bool>> ValidateAddressFormatAsync(string address);
    
    /// <summary>
    /// Get chain-specific network information
    /// </summary>
    protected abstract Task<OASISResult<Dictionary<string, object>>> GetChainInfoInternalAsync();
    
    // Common interface implementations
    public abstract Task<OASISResult<string>> GetBalanceAsync(string address, string? tokenId = null, CancellationToken ct = default);
    public abstract Task<OASISResult<bool>> ValidateAddressAsync(string address, CancellationToken ct = default);
    public abstract Task<OASISResult<string>> MintAsync(string tokenUri, int amount, string assetType, string walletAddress, CancellationToken ct = default);
    public abstract Task<OASISResult<string>> BurnAsync(string tokenId, int amount, string walletAddress, CancellationToken ct = default);
    public abstract Task<OASISResult<string>> TransferAsync(string tokenId, string fromAddress, string toAddress, int amount, CancellationToken ct = default);
    public abstract Task<OASISResult<string>> ExchangeAsync(string sourceTokenId, string targetTokenId, string exchangeRate, string walletAddress, CancellationToken ct = default);
    public abstract Task<OASISResult<string>> SwapAsync(string tokenIn, string tokenOut, decimal amountIn, decimal minAmountOut, string walletAddress, CancellationToken ct = default);
    public abstract Task<OASISResult<Dictionary<string, object>>> GetTokenMetadataAsync(string tokenId, CancellationToken ct = default);
    public abstract Task<OASISResult<List<Dictionary<string, object>>>> GetTokensByOwnerAsync(string ownerAddress, CancellationToken ct = default);
    public abstract Task<OASISResult<Dictionary<string, object>>> GetTransactionStatusAsync(string txHash, CancellationToken ct = default);
    public abstract Task<OASISResult<string>> DeployContractAsync(byte[] contractCode, string walletAddress, Dictionary<string, object>? args = null, CancellationToken ct = default);
    public abstract Task<OASISResult<object>> CallContractAsync(string contractAddress, string method, Dictionary<string, object> args, string walletAddress, CancellationToken ct = default);
    
    public async Task<OASISResult<Dictionary<string, object>>> GetChainInfoAsync(CancellationToken ct = default)
    {
        try
        {
            return await GetChainInfoInternalAsync();
        }
        catch (Exception ex)
        {
            return CreateErrorResponse<Dictionary<string, object>>($"Failed to get {ChainType} chain info", ex);
        }
    }
}

/// <summary>
/// Exception for blockchain provider operations
/// </summary>
public class BlockchainProviderException : Exception
{
    public BlockchainProviderException(string message, Exception? innerException = null) 
        : base(message, innerException) { }
}