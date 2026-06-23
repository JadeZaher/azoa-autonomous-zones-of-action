using System.Collections.Concurrent;
using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Core;

public interface IBlockchainProviderFactory
{
    IBlockchainProvider GetProvider(string chainType, ChainNetwork network);
    IBlockchainProvider GetDefaultProvider();
    IEnumerable<IBlockchainProvider> GetAllEnabledProviders();
    bool TryGetModule<T>(IBlockchainProvider provider, out T? module) where T : class, IBlockchainProviderModule;
}

public class BlockchainProviderFactory : IBlockchainProviderFactory
{
    private readonly IReadOnlyDictionary<string, Func<IBlockchainProvider>> _providerFactories;
    private readonly BlockchainConfig _config;
    private readonly ConcurrentDictionary<string, IBlockchainProvider> _activeProviders = new();

    public BlockchainProviderFactory(IEnumerable<IBlockchainProvider> registeredProviders, IConfiguration config)
    {
        _config = config.GetSection("Blockchain").Get<BlockchainConfig>() ?? new BlockchainConfig();

        var factories = new Dictionary<string, Func<IBlockchainProvider>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rp in registeredProviders)
        {
            factories[rp.ChainType] = () => rp;
        }
        _providerFactories = factories;
    }

    /// <summary>
    /// Global simulated-mode chain type (db-only-null-provider track). Matches
    /// <see cref="AZOA.WebAPI.Providers.Blockchain.Simulated.SimulatedBlockchainProvider.ChainType"/>.
    /// </summary>
    private const string SimulatedChainType = "Simulated";

    /// <summary>True when <c>Blockchain:Mode</c> is "Simulated" (case-insensitive).</summary>
    private bool IsSimulatedMode =>
        string.Equals(_config.Mode, SimulatedChainType, StringComparison.OrdinalIgnoreCase);

    public IBlockchainProvider GetProvider(string chainType, ChainNetwork network)
    {
        // Global simulated mode (db-only-null-provider D2/D3): short-circuit every
        // chain to the SimulatedBlockchainProvider so dev/test/demo and no-chain
        // tenants get deterministic, marked, network-free settlement regardless of
        // the requested chain. The Live-mode throw below is preserved for
        // genuinely-unregistered chains.
        if (IsSimulatedMode)
            chainType = SimulatedChainType;

        if (!_providerFactories.TryGetValue(chainType, out var factory))
            throw new InvalidOperationException($"No provider registered for chain type: {chainType}");

        var key = $"{chainType}:{network}";
        return _activeProviders.GetOrAdd(key, _ =>
        {
            var provider = factory();
            var chainConfig = _config.Chains.FirstOrDefault(c =>
                c.ChainType.Equals(chainType, StringComparison.OrdinalIgnoreCase));

            var networkConfig = network switch
            {
                ChainNetwork.Devnet => chainConfig?.Devnet,
                ChainNetwork.Testnet => chainConfig?.Testnet,
                ChainNetwork.Mainnet => chainConfig?.Mainnet,
                _ => chainConfig?.Devnet
            };

            provider.Initialize(networkConfig ?? new BlockchainNetworkConfig(), network);
            return provider;
        });
    }

    public IBlockchainProvider GetDefaultProvider()
    {
        return GetProvider(_config.DefaultChain, _config.DefaultNetwork);
    }

    public IEnumerable<IBlockchainProvider> GetAllEnabledProviders()
    {
        foreach (var chain in _config.Chains.Where(c => c.Devnet.IsEnabled || c.Testnet.IsEnabled || c.Mainnet.IsEnabled))
        {
            if (_providerFactories.ContainsKey(chain.ChainType))
            {
                var network = chain.Mainnet.IsEnabled ? ChainNetwork.Mainnet
                    : chain.Testnet.IsEnabled ? ChainNetwork.Testnet
                    : ChainNetwork.Devnet;
                yield return GetProvider(chain.ChainType, network);
            }
        }
    }

    public bool TryGetModule<T>(IBlockchainProvider provider, out T? module) where T : class, IBlockchainProviderModule
    {
        if (provider is T t)
        {
            module = t;
            return true;
        }
        module = null;
        return false;
    }
}
