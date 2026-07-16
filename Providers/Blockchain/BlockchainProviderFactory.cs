using System.Collections.Concurrent;
using System.Threading;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Providers.Blockchain.Base;

namespace AZOA.WebAPI.Providers.Blockchain;

public interface IBlockchainProviderFactory
{
    IBlockchainProvider GetProvider(string chainType, ChainNetwork network);
    IBlockchainProvider GetDefaultProvider();
    IEnumerable<IBlockchainProvider> GetAllEnabledProviders();
    bool TryGetModule<T>(IBlockchainProvider provider, out T? module) where T : class, IBlockchainProviderModule;
}

public sealed class BlockchainProviderNotFoundException : InvalidOperationException
{
    public BlockchainProviderNotFoundException(string chainType)
        : base($"No provider is registered for chain type '{chainType}'.")
    {
        ChainType = chainType;
    }

    public string ChainType { get; }
}

/// <summary>Creates an independent provider instance for one chain/network binding.</summary>
public sealed class BlockchainProviderRegistration
{
    public BlockchainProviderRegistration(string chainType, Func<IBlockchainProvider> create)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chainType);
        ArgumentNullException.ThrowIfNull(create);

        ChainType = chainType;
        Create = create;
    }

    public string ChainType { get; }
    public Func<IBlockchainProvider> Create { get; }
}

public class BlockchainProviderFactory : IBlockchainProviderFactory
{
    /// <summary>
    /// Fail-fast boot guard (H1): refuses to start when <c>Blockchain:Mode=Simulated</c>
    /// in a Production host environment, since simulated settlement (fake <c>sim:tx:</c>
    /// results, no network I/O) must never leak into prod. Dev/IntegrationTest/other
    /// non-Production environments are unaffected. Mirrors the secret/CORS/durability
    /// boot guards in <c>Program.cs</c>.
    /// </summary>
    public static void GuardAgainstSimulatedModeInProduction(IConfiguration config, bool isProductionEnvironment)
    {
        if (!isProductionEnvironment)
            return;

        var mode = config.GetSection("Blockchain").Get<BlockchainConfig>()?.Mode ?? "Live";
        if (string.Equals(mode, SimulatedChainType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Blockchain:Mode is set to \"Simulated\" in a Production environment. " +
                "Simulated settlement (fake sim:tx: results, no network I/O) must never " +
                "run in Production. Set Blockchain:Mode=Live (or unset it) before starting.");
    }

    private readonly IReadOnlyDictionary<string, BlockchainProviderRegistration> _providerFactories;
    private readonly BlockchainConfig _config;
    private readonly ConcurrentDictionary<ProviderNetworkKey, Lazy<IBlockchainProvider>> _activeProviders = new();

    public BlockchainProviderFactory(
        IEnumerable<BlockchainProviderRegistration> registeredProviders,
        IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(registeredProviders);
        ArgumentNullException.ThrowIfNull(config);

        _config = config.GetSection("Blockchain").Get<BlockchainConfig>() ?? new BlockchainConfig();

        var factories = new Dictionary<string, BlockchainProviderRegistration>(StringComparer.OrdinalIgnoreCase);
        foreach (var registration in registeredProviders)
        {
            if (!factories.TryAdd(registration.ChainType, registration))
                throw new InvalidOperationException($"Duplicate blockchain provider registration for '{registration.ChainType}'.");
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
        ArgumentException.ThrowIfNullOrWhiteSpace(chainType);
        EnsureSupportedNetwork(network);

        // Global simulated mode (db-only-null-provider D2/D3): short-circuit every
        // chain to the SimulatedBlockchainProvider so dev/test/demo and no-chain
        // tenants get deterministic, marked, network-free settlement regardless of
        // the requested chain. The Live-mode throw below is preserved for
        // genuinely-unregistered chains.
        if (IsSimulatedMode)
            chainType = SimulatedChainType;

        if (!_providerFactories.TryGetValue(chainType, out var registration))
            throw new BlockchainProviderNotFoundException(chainType);

        var networkConfig = ResolveEnabledNetworkConfig(registration.ChainType, network);
        var key = new ProviderNetworkKey(registration.ChainType, network);
        return _activeProviders.GetOrAdd(
            key,
            _ => new Lazy<IBlockchainProvider>(
                () => CreateInitializedProvider(registration, networkConfig, network),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
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

    private IBlockchainProvider CreateInitializedProvider(
        BlockchainProviderRegistration registration,
        BlockchainNetworkConfig networkConfig,
        ChainNetwork network)
    {
        var provider = registration.Create();
        if (!string.Equals(provider.ChainType, registration.ChainType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Blockchain provider registration '{registration.ChainType}' created '{provider.ChainType}'.");

        if (provider is BaseBlockchainProvider managedProvider)
            managedProvider.InitializeForFactory(networkConfig, network);
        else
            provider.Initialize(networkConfig, network);
        return provider;
    }

    private BlockchainNetworkConfig ResolveEnabledNetworkConfig(string chainType, ChainNetwork network)
    {
        if (IsSimulatedMode && string.Equals(chainType, SimulatedChainType, StringComparison.OrdinalIgnoreCase))
            return new BlockchainNetworkConfig { IsEnabled = true };

        var chainConfig = _config.Chains.FirstOrDefault(chain =>
            chain.ChainType.Equals(chainType, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No blockchain configuration is registered for chain '{chainType}'.");

        var networkConfig = network switch
        {
            ChainNetwork.Devnet => chainConfig.Devnet,
            ChainNetwork.Testnet => chainConfig.Testnet,
            ChainNetwork.Mainnet => chainConfig.Mainnet,
            _ => throw new ArgumentOutOfRangeException(nameof(network), network, "Unsupported blockchain network."),
        };

        if (!networkConfig.IsEnabled)
            throw new InvalidOperationException($"Network {network} is disabled for chain '{chainType}'.");

        return networkConfig;
    }

    private static void EnsureSupportedNetwork(ChainNetwork network)
    {
        if (!Enum.IsDefined(network))
            throw new ArgumentOutOfRangeException(nameof(network), network, "Unsupported blockchain network.");
    }

    private readonly record struct ProviderNetworkKey(string ChainType, ChainNetwork Network);
}
