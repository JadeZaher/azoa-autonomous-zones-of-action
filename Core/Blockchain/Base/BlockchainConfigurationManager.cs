using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Providers.Blockchain.Base;

/// <summary>
/// Resolves blockchain network configuration from appsettings.
/// </summary>
public class BlockchainConfigurationManager
{
    private readonly IConfiguration _config;

    public BlockchainConfigurationManager(IConfiguration config)
    {
        _config = config;
    }

    /// <summary>
    /// Get configuration for a specific chain and network.
    /// </summary>
    public BlockchainNetworkConfig GetNetworkConfig(string chainType, ChainNetwork network)
    {
        var chains = _config.GetSection("Blockchain:Chains").Get<List<BlockchainChainConfig>>()
                     ?? throw new InvalidOperationException("No blockchain chains configured.");

        var chainConfig = chains.FirstOrDefault(c =>
            c.ChainType.Equals(chainType, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No configuration found for chain: {chainType}");

        var networkConfig = network switch
        {
            ChainNetwork.Devnet => chainConfig.Devnet,
            ChainNetwork.Testnet => chainConfig.Testnet,
            ChainNetwork.Mainnet => chainConfig.Mainnet,
            _ => chainConfig.Devnet
        };

        if (networkConfig == null || !networkConfig.IsEnabled)
            throw new InvalidOperationException(
                $"Network {network} is not configured or disabled for chain {chainType}");

        return networkConfig;
    }

    /// <summary>
    /// Get the default network for a chain. Absent config means local dev (Devnet); a
    /// PRESENT but unrecognised value throws rather than silently pinning to Devnet —
    /// see <see cref="ChainNetworkResolution"/>.
    /// </summary>
    public ChainNetwork GetDefaultNetwork(string? chainType = null)
        => ChainNetworkResolution.FromConfiguration(_config);

    /// <summary>
    /// Get all available chain configurations.
    /// </summary>
    public List<BlockchainChainConfig> GetAvailableChains()
    {
        return _config.GetSection("Blockchain:Chains").Get<List<BlockchainChainConfig>>()
               ?? new List<BlockchainChainConfig>();
    }

    /// <summary>
    /// Validate that a chain's configuration is well-formed.
    /// Returns (isValid, errors).
    /// </summary>
    public (bool IsValid, List<string> Errors) ValidateChainConfig(string chainType, ChainNetwork network)
    {
        var errors = new List<string>();

        try
        {
            var config = GetNetworkConfig(chainType, network);

            if (string.IsNullOrWhiteSpace(config.NodeUrl))
                errors.Add($"NodeUrl is required for {chainType} {network}");

            if (config.TimeoutMs is <= 0)
                errors.Add($"TimeoutMs must be positive for {chainType} {network}");
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        return (errors.Count == 0, errors);
    }
}
