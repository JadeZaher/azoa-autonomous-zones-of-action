using OASIS.WebAPI.Models.Config;

namespace OASIS.WebAPI.Providers.Blockchain.Base;

/// <summary>
/// Manages blockchain network configuration and provides chain-specific settings
/// </summary>
public class BlockchainConfigurationManager
{
    private readonly IConfiguration _config;
    
    public BlockchainConfigurationManager(IConfiguration config)
    {
        _config = config;
    }
    
    /// <summary>
    /// Get configuration for a specific blockchain and network
    /// </summary>
    public BlockchainChainConfig GetChainConfiguration(string chainType, ChainNetwork network)
    {
        var chains = _config.GetSection("Blockchain:Chains").Get<List<BlockchainChainConfig>>() ?? new List<BlockchainChainConfig>();
        var chainConfig = chains.FirstOrDefault(c => c.ChainType.Equals(chainType, StringComparison.OrdinalIgnoreCase));
        
        if (chainConfig == null)
            throw new InvalidOperationException($"Configuration not found for chain: {chainType}");
        
        BlockchainNetworkConfig? networkConfig = null;
        
        switch (network)
        {
            case ChainNetwork.Devnet:
                networkConfig = chainConfig.Devnet;
                break;
            case ChainNetwork.Testnet:
                networkConfig = chainConfig.Testnet;
                break;
            case ChainNetwork.Mainnet:
                networkConfig = chainConfig.Mainnet;
                break;
        }
        
        if (networkConfig == null)
            throw new InvalidOperationException($"Network configuration not found for {chainType} {network}");
        
        if (!networkConfig.IsEnabled)
            throw new InvalidOperationException($"Network {network} is disabled for chain {chainType}");
        
        return new BlockchainChainConfig
        {
            ChainType = chainConfig.ChainType,
            NodeUrl = networkConfig.NodeUrl,
            IndexerUrl = networkConfig.IndexerUrl,
            ApiToken = networkConfig.ApiToken ?? "",
            TimeoutMs = networkConfig.TimeoutMs ?? 30000,
            RetryCount = networkConfig.RetryCount ?? 3,
            IsEnabled = networkConfig.IsEnabled ?? true
        };
    }
    
    /// <summary>
    /// Get default network for a chain
    /// </summary>
    public ChainNetwork GetDefaultNetworkForChain(string chainType)
    {
        var defaultNetwork = _config.GetValue<string>("Blockchain:DefaultNetwork")?.ToLower();
        
        if (!string.IsNullOrEmpty(defaultNetwork))
        {
            return defaultNetwork switch
            {
                "testnet" => ChainNetwork.Testnet,
                "mainnet" => ChainNetwork.Mainnet,
                _ => ChainNetwork.Devnet
            };
        }
        
        // Default to devnet if not specified
        return ChainNetwork.Devnet;
    }
    
    /// <summary>
    /// Get all available chains
    /// </summary>
    public List<BlockchainChainConfig> GetAvailableChains()
    {
        return _config.GetSection("Blockchain:Chains").Get<List<BlockchainChainConfig>>() ?? new List<BlockchainChainConfig>();
    }
    
    /// <summary>
    /// Validate configuration for a specific chain
    /// </summary>
    public (bool IsValid, List<string> Errors) ValidateChainConfiguration(string chainType, ChainNetwork network)
    {
        var errors = new List<string>();
        
        try
        {
            var config = GetChainConfiguration(chainType, network);
            
            if (string.IsNullOrEmpty(config.NodeUrl))
            {
                errors.Add($"Node URL is required for {chainType} {network}");
            }
            
            if (config.TimeoutMs <= 0)
            {
                errors.Add($"Timeout must be positive for {chainType} {network}");
            }
            
            // Chain-specific validation
            if (chainType.Equals("Algorand", StringComparison.OrdinalIgnoreCase))
            {
                if (config.NodeUrl != null && !config.NodeUrl.Contains("algorand"))
                {
                    errors.Add($"Invalid Algorand node URL: {config.NodeUrl}");
                }
            }
            else if (chainType.Equals("Solana", StringComparison.OrdinalIgnoreCase))
            {
                if (config.NodeUrl != null && !config.NodeUrl.Contains("solana"))
                {
                    errors.Add($"Invalid Solana node URL: {config.NodeUrl}");
                }
            }
            
            return (errors.Count == 0, errors);
        }
        catch (Exception ex)
        {
            errors.Add($"Configuration validation failed: {ex.Message}");
            return (false, errors);
        }
    }
}