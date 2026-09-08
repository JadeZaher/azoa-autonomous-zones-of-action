using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Providers.Blockchain.Base;

namespace AZOA.WebAPI.Providers.Blockchain;

/// <summary>
/// Resolves a bound <see cref="IValueRailProvider"/> for one rail + environment,
/// whether or not that rail is a blockchain (decision D9).
/// </summary>
/// <remarks>
/// Split out of <see cref="IBlockchainProviderFactory"/> because the chain-typed
/// factory made a non-chain rail impossible to register at all: registration was
/// <c>Func&lt;IBlockchainProvider&gt;</c> and resolution returned
/// <see cref="IBlockchainProvider"/>, so a fiat rail — which is not a chain and has no
/// contracts, faucet, or wrapped assets — could not be expressed. See
/// <c>Providers/Blockchain/AGENTS.md</c> §value-rail-factory.
/// </remarks>
public interface IValueRailProviderFactory
{
    /// <summary>
    /// The bound provider for <paramref name="railId"/> in <paramref name="environment"/>.
    /// Throws <see cref="ValueRailProviderNotFoundException"/> when nothing is registered
    /// for the rail, and <see cref="InvalidOperationException"/> when the rail is
    /// registered but that environment is disabled.
    /// </summary>
    IValueRailProvider GetRailProvider(string railId, RailEnvironment environment);

    /// <summary>Non-throwing form of <see cref="GetRailProvider"/> for capability probes.</summary>
    bool TryGetRailProvider(string railId, RailEnvironment environment, [NotNullWhen(true)] out IValueRailProvider? provider);

    /// <summary>Every registered rail id — chain and non-chain alike. Names only.</summary>
    IReadOnlyCollection<string> RegisteredRailIds { get; }

    /// <summary>
    /// The rail an operation settles on when it names none. Configured, not guessed:
    /// callers that need "the platform's rail" must not each pick their own answer.
    /// </summary>
    string DefaultRailId { get; }
}

public interface IBlockchainProviderFactory : IValueRailProviderFactory
{
    /// <summary>
    /// The configured network (<c>Blockchain:DefaultNetwork</c>) a chain call binds to
    /// when the caller has no network of its own. Exists so no call site hardcodes
    /// <see cref="ChainNetwork.Devnet"/> — which silently pins production to devnet.
    /// </summary>
    ChainNetwork DefaultNetwork { get; }

    IBlockchainProvider GetProvider(string chainType, ChainNetwork network);
    IBlockchainProvider GetDefaultProvider();
    IEnumerable<IBlockchainProvider> GetAllEnabledProviders();
    bool TryGetModule<T>(IBlockchainProvider provider, out T? module) where T : class, IBlockchainProviderModule;
}

public class ValueRailProviderNotFoundException : InvalidOperationException
{
    public ValueRailProviderNotFoundException(string railId)
        : base($"No provider is registered for value rail '{railId}'.")
    {
        RailId = railId;
    }

    protected ValueRailProviderNotFoundException(string railId, string message) : base(message)
    {
        RailId = railId;
    }

    public string RailId { get; }
}

public sealed class BlockchainProviderNotFoundException : ValueRailProviderNotFoundException
{
    public BlockchainProviderNotFoundException(string chainType)
        : base(chainType, $"No provider is registered for chain type '{chainType}'.")
    {
    }

    public string ChainType => RailId;
}

/// <summary>
/// Creates an independent provider instance for one rail/environment binding, and says
/// how that rail's endpoint settings are resolved.
/// </summary>
/// <remarks>
/// A chain rail leaves <see cref="ResolveEndpoint"/> null and takes its settings from
/// <c>Blockchain:Chains</c> (unchanged behaviour). A non-chain rail supplies its own
/// resolver, because <c>Blockchain:Chains</c> is keyed by Devnet/Testnet/Mainnet and a
/// fiat rail has test and live environments only — the config shape, not the provider
/// type, is the second thing that used to make a fiat rail unregistrable.
/// </remarks>
public class ValueRailProviderRegistration
{
    public ValueRailProviderRegistration(
        string railId,
        Func<IValueRailProvider> create,
        Func<RailEnvironment, BlockchainNetworkConfig?> resolveEndpoint)
        : this(railId, create, isChainRail: false, resolveEndpoint)
    {
        ArgumentNullException.ThrowIfNull(resolveEndpoint);
    }

    private protected ValueRailProviderRegistration(
        string railId,
        Func<IValueRailProvider> create,
        bool isChainRail,
        Func<RailEnvironment, BlockchainNetworkConfig?>? resolveEndpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(railId);
        ArgumentNullException.ThrowIfNull(create);

        RailId = railId;
        Create = create;
        IsChainRail = isChainRail;
        ResolveEndpoint = resolveEndpoint;
    }

    /// <summary>The rail this registration serves.</summary>
    public string RailId { get; }

    /// <summary>Produces a fresh, UNBOUND provider instance. The factory binds it.</summary>
    public Func<IValueRailProvider> Create { get; }

    /// <summary>
    /// True when the created provider is also an <see cref="IBlockchainProvider"/>, so the
    /// factory may hand it back through the chain-typed API.
    /// </summary>
    public bool IsChainRail { get; }

    /// <summary>
    /// The rail's own environment → endpoint mapping, or null for a chain rail (which uses
    /// <c>Blockchain:Chains</c>). Returning null for an environment means that environment
    /// is disabled for this rail.
    /// </summary>
    public Func<RailEnvironment, BlockchainNetworkConfig?>? ResolveEndpoint { get; }
}

/// <summary>A <see cref="ValueRailProviderRegistration"/> whose rail is a blockchain.</summary>
public sealed class BlockchainProviderRegistration : ValueRailProviderRegistration
{
    public BlockchainProviderRegistration(string chainType, Func<IBlockchainProvider> create)
        : base(chainType, create, isChainRail: true, resolveEndpoint: null)
    {
    }

    public string ChainType => RailId;
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

    /// <summary>
    /// Fail-fast boot guard (Phase 0 capability-honesty): rejects a config that
    /// advertises (enables Devnet, Testnet, or Mainnet for) a chain with no registered
    /// provider — e.g. <c>Blockchain:Chains</c> lists "Ethereum" as enabled but no
    /// <c>EthereumProvider</c> is registered, so resolving it throws
    /// <see cref="BlockchainProviderNotFoundException"/> at request time instead of at boot.
    /// This MUST read <c>Blockchain:Chains</c> directly and diff it against the registered
    /// chain types: <see cref="GetAllEnabledProviders"/> silently SKIPS unregistered chains,
    /// so routing this guard through it would make the exact defect it exists to catch
    /// invisible. Mirrors <see cref="GuardAgainstSimulatedModeInProduction"/>.
    /// </summary>
    public static void GuardAgainstUnregisteredEnabledChains(
        IConfiguration config,
        IEnumerable<string> registeredChainTypes)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(registeredChainTypes);

        var chains = config.GetSection("Blockchain").Get<BlockchainConfig>()?.Chains
            ?? new List<BlockchainChainConfig>();
        var registered = new HashSet<string>(registeredChainTypes, StringComparer.OrdinalIgnoreCase);

        var unregisteredEnabledChains = chains
            .Where(chain => chain.Devnet.IsEnabled || chain.Testnet.IsEnabled || chain.Mainnet.IsEnabled)
            .Select(chain => chain.ChainType)
            .Where(chainType => !registered.Contains(chainType))
            .ToList();

        if (unregisteredEnabledChains.Count > 0)
            throw new InvalidOperationException(
                "Blockchain:Chains config enables the following chain(s) with no registered provider: " +
                $"{string.Join(", ", unregisteredEnabledChains)}. Resolving " +
                $"{(unregisteredEnabledChains.Count == 1 ? "it" : "them")} at runtime throws " +
                $"{nameof(BlockchainProviderNotFoundException)}. Either register a provider for " +
                $"{(unregisteredEnabledChains.Count == 1 ? "it" : "them")} or set IsEnabled=false for every " +
                "network under that chain in appsettings.");
    }

    private readonly IReadOnlyDictionary<string, ValueRailProviderRegistration> _providerFactories;
    private readonly BlockchainConfig _config;
    private readonly ConcurrentDictionary<ProviderRailKey, Lazy<IValueRailProvider>> _activeProviders = new();

    public BlockchainProviderFactory(
        IEnumerable<ValueRailProviderRegistration> registeredProviders,
        IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(registeredProviders);
        ArgumentNullException.ThrowIfNull(config);

        _config = config.GetSection("Blockchain").Get<BlockchainConfig>() ?? new BlockchainConfig();

        var factories = new Dictionary<string, ValueRailProviderRegistration>(StringComparer.OrdinalIgnoreCase);
        foreach (var registration in registeredProviders)
        {
            if (!factories.TryAdd(registration.RailId, registration))
                throw new InvalidOperationException($"Duplicate value rail provider registration for '{registration.RailId}'.");
        }
        _providerFactories = factories;
        RegisteredRailIds = factories.Values.Select(item => item.RailId).ToArray();
    }

    /// <summary>
    /// Global simulated-mode chain type (db-only-null-provider track). Matches
    /// <see cref="AZOA.WebAPI.Providers.Blockchain.Simulated.SimulatedBlockchainProvider.ChainType"/>.
    /// </summary>
    private const string SimulatedChainType = "Simulated";

    /// <summary>True when <c>Blockchain:Mode</c> is "Simulated" (case-insensitive).</summary>
    private bool IsSimulatedMode =>
        string.Equals(_config.Mode, SimulatedChainType, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public IReadOnlyCollection<string> RegisteredRailIds { get; }

    /// <inheritdoc/>
    public string DefaultRailId => _config.DefaultChain;

    /// <inheritdoc/>
    public ChainNetwork DefaultNetwork => _config.DefaultNetwork;

    /// <inheritdoc/>
    public IValueRailProvider GetRailProvider(string railId, RailEnvironment environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(railId);

        // Global simulated mode (db-only-null-provider D2/D3): short-circuit every CHAIN
        // rail to the SimulatedBlockchainProvider so dev/test/demo and no-chain tenants
        // get deterministic, marked, network-free settlement regardless of the requested
        // chain. A non-chain rail is NOT redirected — a simulated blockchain is not a
        // stand-in for a payment processor, and pretending otherwise would fake money.
        if (IsSimulatedMode && IsChainRailRequest(railId))
            railId = SimulatedChainType;

        if (!_providerFactories.TryGetValue(railId, out var registration))
            throw RailNotFound(railId);

        var binding = ResolveEnabledBinding(registration, environment);
        var key = new ProviderRailKey(registration.RailId, binding.Environment.Name);
        var lazy = _activeProviders.GetOrAdd(
            key,
            _ => new Lazy<IValueRailProvider>(
                () => CreateInitializedProvider(registration, binding),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return lazy.Value;
        }
        catch
        {
            // ExecutionAndPublication CACHES the exception forever: once a creation throws, every
            // later resolve of that (rail, environment) rethrows the SAME failure until the process
            // restarts. For a chain provider built from static config that was invisible; for a rail
            // whose creation reaches into the container it means one transient startup-order failure
            // permanently strands the rail — and a payment rail that is permanently unavailable
            // after one hiccup takes every sponsor with it. Evicting the faulted entry (only when
            // it is still the one we published) lets the next call rebuild. The exception still
            // propagates to THIS caller: a failure is never swallowed, only un-cached.
            _activeProviders.TryRemove(new KeyValuePair<ProviderRailKey, Lazy<IValueRailProvider>>(key, lazy));
            throw;
        }
    }

    /// <inheritdoc/>
    public bool TryGetRailProvider(
        string railId, RailEnvironment environment, [NotNullWhen(true)] out IValueRailProvider? provider)
    {
        provider = null;
        if (string.IsNullOrWhiteSpace(railId))
            return false;

        try
        {
            provider = GetRailProvider(railId, environment);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Unregistered rail, or an environment disabled for it. A probe wants a
            // verdict, not an exception; the throwing overload stays for call sites
            // that treat absence as a fault.
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public IBlockchainProvider GetProvider(string chainType, ChainNetwork network)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chainType);
        EnsureSupportedNetwork(network);

        var provider = GetRailProvider(chainType, RailEnvironment.ForChain(network));
        if (provider is not IBlockchainProvider chainProvider)
            throw new InvalidOperationException(
                $"Value rail '{chainType}' is registered but is not a blockchain, so it cannot be resolved "
                + $"through the chain-typed API. Use {nameof(GetRailProvider)} and depend on "
                + $"{nameof(IValueRailProvider)} for a rail that is not a chain.");

        return chainProvider;
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

    /// <summary>
    /// Whether a requested rail id denotes a chain, for the simulated-mode redirect.
    /// An UNREGISTERED id counts as a chain so the pre-existing behaviour is preserved:
    /// in simulated mode any unknown chain resolves to the simulated provider, and the
    /// Live-mode throw still catches genuinely-unregistered chains.
    /// </summary>
    private bool IsChainRailRequest(string railId) =>
        !_providerFactories.TryGetValue(railId, out var registration) || registration.IsChainRail;

    private static ValueRailProviderNotFoundException RailNotFound(string railId) =>
        new BlockchainProviderNotFoundException(railId);

    private IValueRailProvider CreateInitializedProvider(
        ValueRailProviderRegistration registration, ValueRailBinding binding)
    {
        var provider = registration.Create();

        if (registration.IsChainRail && provider is not IBlockchainProvider)
            throw new InvalidOperationException(
                $"Value rail registration '{registration.RailId}' is declared a chain rail but created a "
                + $"provider that does not implement {nameof(IBlockchainProvider)}.");

        // A chain rail is identified by its ChainType — that is the name the chain-typed
        // API, the operation record, and the config section are all keyed on. A non-chain
        // rail has only RailId. Checking each in its own vocabulary keeps a registration
        // that produced the wrong provider loud without demanding a second spelling.
        var createdRailId = registration.IsChainRail
            ? ((IBlockchainProvider)provider).ChainType
            : provider.RailId;
        if (!string.Equals(createdRailId, registration.RailId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Value rail registration '{registration.RailId}' created '{createdRailId}'.");

        // Three binding paths, narrowest first:
        //  1. BaseBlockchainProvider owns a stricter, factory-only entry point that also
        //     marks the instance non-rebindable, so a cached binding cannot be retargeted;
        //  2. any other chain provider binds through the chain-shaped Initialize, which is
        //     the call every existing provider and test double already implements;
        //  3. a rail that is not a chain binds through the rail-generic InitializeRail —
        //     it has no ChainNetwork to be handed.
        var isChainNetwork = binding.Environment.TryToChainNetwork(out var network);
        if (provider is BaseBlockchainProvider managedProvider && isChainNetwork)
            managedProvider.InitializeForFactory(binding.Endpoint, network);
        else if (provider is IBlockchainProvider chainProvider && isChainNetwork)
            chainProvider.Initialize(binding.Endpoint, network);
        else
            provider.InitializeRail(binding);

        return provider;
    }

    private ValueRailBinding ResolveEnabledBinding(
        ValueRailProviderRegistration registration, RailEnvironment environment)
    {
        if (!registration.IsChainRail)
            return ResolveNonChainBinding(registration, environment);

        if (!environment.TryToChainNetwork(out var network))
            throw new InvalidOperationException(
                $"Chain rail '{registration.RailId}' can only bind to a chain network "
                + $"(Devnet/Testnet/Mainnet); '{environment}' is not one.");

        return new ValueRailBinding(environment, ResolveEnabledNetworkConfig(registration.RailId, network));
    }

    /// <summary>
    /// A non-chain rail brings its own environment → endpoint mapping, because
    /// <c>Blockchain:Chains</c> only knows Devnet/Testnet/Mainnet. A null result means
    /// the rail exists but that environment is switched off.
    /// </summary>
    private static ValueRailBinding ResolveNonChainBinding(
        ValueRailProviderRegistration registration, RailEnvironment environment)
    {
        var endpoint = registration.ResolveEndpoint!(environment);
        if (endpoint is null || !endpoint.IsEnabled)
            throw new InvalidOperationException(
                $"Environment {environment} is disabled for value rail '{registration.RailId}'.");

        return new ValueRailBinding(environment, endpoint);
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

    /// <summary>
    /// One cached instance per (rail, environment). Keyed on the environment NAME rather
    /// than <see cref="ChainNetwork"/> so a rail whose environments are not chain networks
    /// still gets one independent instance per environment.
    /// </summary>
    private readonly record struct ProviderRailKey(string RailId, string EnvironmentName);
}
