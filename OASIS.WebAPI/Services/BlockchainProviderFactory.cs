using Microsoft.Extensions.Configuration;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Providers.Blockchain;
using OASIS.WebAPI.Providers.Blockchain.Algorand;
using OASIS.WebAPI.Providers.Blockchain.Solana;

namespace OASIS.WebAPI.Services;

/// <summary>
/// Factory for creating and managing blockchain providers
/// </summary>
public class BlockchainProviderFactory : IBlockchainProviderFactory
{
    private readonly IConfiguration _config;
    private readonly Dictionary<string, IBlockchainProvider> _providers;
    private readonly BlockchainConfigurationManager _configManager;

    public BlockchainProviderFactory(IConfiguration config)
    {
        _config = config;
        _configManager = new BlockchainConfigurationManager(config);
        _providers = new Dictionary<string, IBlockchainProvider>(StringComparer.OrdinalIgnoreCase);
        
        // Initialize all available providers
        InitializeProviders();
    }

    /// <summary>
    /// Get a blockchain provider by chain type
    /// </summary>
    /// <param name="chainType">The type of blockchain (Algorand, Solana, etc.)</param>
    /// <returns>An instance of the blockchain provider</returns>
    public IBlockchainProvider GetProvider(string chainType)
    {
        if (string.IsNullOrWhiteSpace(chainType))
        {
            throw new ArgumentException("Chain type is required", nameof(chainType));
        }

        var normalizedChainType = chainType.ToLower();
        
        if (!_providers.ContainsKey(normalizedChainType))
        {
            throw new NotSupportedException($"Blockchain provider for '{chainType}' is not supported");
        }

        return _providers[normalizedChainType];
    }

    /// <summary>
    /// Get all available blockchain providers
    /// </summary>
    /// <returns>A dictionary of available providers</returns>
    public Dictionary<string, IBlockchainProvider> GetAllProviders()
    {
        return new Dictionary<string, IBlockchainProvider>(_providers);
    }

    /// <summary>
    /// Check if a provider is available for the specified chain type
    /// </summary>
    /// <param name="chainType">The type of blockchain to check</param>
    /// <returns>True if the provider is available, false otherwise</returns>
    public bool IsProviderAvailable(string chainType)
    {
        if (string.IsNullOrWhiteSpace(chainType))
            return false;

        var normalizedChainType = chainType.ToLower();
        return _providers.ContainsKey(normalizedChainType);
    }

    /// <summary>
    /// Get all available chain types
    /// </summary>
    /// <returns>A list of available chain types</returns>
    public List<string> GetAvailableChainTypes()
    {
        return _providers.Keys.ToList();
    }

    /// <summary>
    /// Initialize all blockchain providers
    /// </summary>
    private void InitializeProviders()
    {
        try
        {
            // Initialize Algorand provider
            InitializeAlgorandProvider();
            
            // Initialize Solana provider
            InitializeSolanaProvider();
            
            // Add more providers here as they are implemented
            
            _logger.LogInformation("Initialized blockchain providers: {Providers}", 
                string.Join(", ", _providers.Keys));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing blockchain providers");
            throw;
        }
    }

    /// <summary>
    /// Initialize the Algorand provider
    /// </summary>
    private void InitializeAlgorandProvider()
    {
        try
        {
            var provider = new AlgorandProvider(_config);
            _providers["algorand"] = provider;
            _logger.LogInformation("Algorand provider initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing Algorand provider");
            // Don't throw - just log the error and continue
            // The provider will be unavailable but the app can still run
        }
    }

    /// <summary>
    /// Initialize the Solana provider
    /// </summary>
    private void InitializeSolanaProvider()
    {
        try
        {
            var provider = new SolanaProvider(_config);
            _providers["solana"] = provider;
            _logger.LogInformation("Solana provider initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing Solana provider");
            // Don't throw - just log the error and continue
            // The provider will be unavailable but the app can still run
        }
    }

    private readonly ILogger<BlockchainProviderFactory> _logger = 
        LoggerFactory.Create(builder => builder.AddConsole())
            .CreateLogger<BlockchainProviderFactory>();
}

/// <summary>
/// Service registration extensions
/// </summary>
public static class BlockchainProviderServiceExtensions
{
    /// <summary>
    /// Add blockchain provider factory to the service collection
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="config">The configuration</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddBlockchainProviders(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<IBlockchainProviderFactory, BlockchainProviderFactory>();
        services.AddSingleton<BlockchainConfigurationManager>();
        
        return services;
    }
}