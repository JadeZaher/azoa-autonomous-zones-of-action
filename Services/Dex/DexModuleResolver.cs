using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Providers.Blockchain;
using Microsoft.Extensions.Caching.Memory;

namespace AZOA.WebAPI.Services.Dex;

/// <summary>Resolves the exact stored chain and concrete network without substituting configured defaults.</summary>
public sealed class DexModuleResolver(IBlockchainProviderFactory providers, IMemoryCache? cache = null)
{
    private const string DescriptionCacheKey = "dex:capabilities";
    private static readonly TimeSpan DescriptionLifetime = TimeSpan.FromSeconds(30);

    public IDexTransactionModule? Resolve(DexNetwork network)
    {
        if (network is null || string.IsNullOrWhiteSpace(network.ChainType) || string.IsNullOrWhiteSpace(network.ProtocolIdentity) ||
            !Enum.TryParse<ChainNetwork>(network.Network, false, out var concrete) || !Enum.IsDefined(concrete) ||
            network.Network != concrete.ToString()) return null;
        return Resolve(network.ChainType, concrete);
    }

    /// <summary>Lists structurally complete modules on every enabled network; describing grammar never implies liquidity or readiness.</summary>
    public async Task<IReadOnlyList<DexCapabilityView>> DescribeAsync(CancellationToken ct = default)
    {
        // Each description costs one node round-trip per enabled network; a short shared cache keeps the public route cheap.
        if (cache?.TryGetValue(DescriptionCacheKey, out IReadOnlyList<DexCapabilityView>? cached) == true && cached is not null) return cached;
        var described = await DescribeUncachedAsync(ct);
        cache?.Set(DescriptionCacheKey, described, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = DescriptionLifetime });
        return described;
    }

    private async Task<IReadOnlyList<DexCapabilityView>> DescribeUncachedAsync(CancellationToken ct)
    {
        var views = new List<DexCapabilityView>();
        var chains = providers.GetAllEnabledProviders().Select(provider => provider.ChainType).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var chain in chains)
            foreach (var network in Enum.GetValues<ChainNetwork>())
            {
                if (Resolve(chain, network) is not { } module) continue;
                var identity = await module.DescribeDexNetworkAsync(ct);
                views.Add(identity.IsError || identity.Result is null
                    ? new(new(module.ChainType, network.ToString(), ""), module.DexCapability, false, identity.Message)
                    : new(identity.Result, module.DexCapability, true));
            }
        return views;
    }

    /// <summary>
    /// RV-L5-11: the module serving an already-admitted swap, ignoring the D-7 pin so a pin flip never strands in-flight
    /// recovery, stage progress or cleanup. New admissions (quotes) always use the pinned <see cref="Resolve(DexNetwork)"/>.
    /// </summary>
    public IDexTransactionModule? ResolveForExisting(DexNetwork network)
    {
        if (network is null || string.IsNullOrWhiteSpace(network.ChainType) || string.IsNullOrWhiteSpace(network.ProtocolIdentity) ||
            !Enum.TryParse<ChainNetwork>(network.Network, false, out var concrete) || !Enum.IsDefined(concrete) ||
            network.Network != concrete.ToString()) return null;
        return providers.TryGetRailProvider(network.ChainType, RailEnvironment.ForChain(concrete), out var rail) && rail is IBlockchainProvider blockchain &&
            ChainModuleActivation.TryGetUngated<IDexTransactionModule>(blockchain, out var module) && module is not null &&
            module.ChainType == network.ChainType && module.ActiveNetwork == concrete ? module : null;
    }

    private IDexTransactionModule? Resolve(string chainType, ChainNetwork network)
        => providers.TryGetRailProvider(chainType, RailEnvironment.ForChain(network), out var rail) && rail is IBlockchainProvider blockchain &&
            providers.TryGetModule<IDexTransactionModule>(blockchain, out var module) && module is not null &&
            module.ChainType == chainType && module.ActiveNetwork == network ? module : null;
}
