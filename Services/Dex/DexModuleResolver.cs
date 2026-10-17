using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Providers.Blockchain;

namespace AZOA.WebAPI.Services.Dex;

/// <summary>Resolves the exact stored chain and concrete network without substituting configured defaults.</summary>
public sealed class DexModuleResolver(IBlockchainProviderFactory providers)
{
    public IDexTransactionModule? Resolve(DexNetwork network)
    {
        if (network is null || string.IsNullOrWhiteSpace(network.ChainType) || string.IsNullOrWhiteSpace(network.ProtocolIdentity) ||
            !Enum.TryParse<ChainNetwork>(network.Network, false, out var concrete) || !Enum.IsDefined(concrete) ||
            network.Network != concrete.ToString() ||
            !providers.TryGetRailProvider(network.ChainType, RailEnvironment.ForChain(concrete), out var rail) || rail is not IBlockchainProvider blockchain ||
            !providers.TryGetModule<IDexTransactionModule>(blockchain, out var module) || module is null ||
            module.ChainType != network.ChainType || module.ActiveNetwork != concrete) return null;
        return module;
    }
}
