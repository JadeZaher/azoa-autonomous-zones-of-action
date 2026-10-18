using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

/// <summary>Resolves the Ethereum recovery module for a network, or null when that network is not enabled on this node.</summary>
public static class EthereumRecoveryResolution
{
    /// <summary>Wire code for an Ethereum network this node does not run (QA M2: was an unhandled 500).</summary>
    public const string UnavailableCode = "ETHEREUM_RECOVERY_UNAVAILABLE";

    public const string UnavailableMessage = "Ethereum recovery is unavailable: Ethereum is not enabled for this network on this node.";

    /// <summary>Non-throwing probe: an unregistered rail and a disabled network both answer null.</summary>
    public static IEthereumRecoveryModule? TryResolveEthereumRecovery(this IBlockchainProviderFactory providers, ChainNetwork network)
        => Enum.IsDefined(network)
           && providers.TryGetRailProvider("Ethereum", RailEnvironment.ForChain(network), out var provider)
            ? provider as IEthereumRecoveryModule
            : null;
}
