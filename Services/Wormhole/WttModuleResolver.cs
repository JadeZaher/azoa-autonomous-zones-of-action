using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Providers.Blockchain;

namespace AZOA.WebAPI.Services.Wormhole;

/// <summary>Resolves a WTT capability for the exact frozen chain and concrete network, never a configured default.</summary>
public sealed class WttModuleResolver(IBlockchainProviderFactory providers, IConfiguration? configuration = null)
{
    /// <summary>X-6 production switch; absent, blank or malformed means off. See Services/Wormhole/AGENTS.md §Activation.</summary>
    public const string EnabledKey = "Wtt:Enabled";

    /// <summary>What a caller is told while WTT admission is switched off.</summary>
    public const string DisabledMessage = "Wormhole token transfers are not enabled on this node; nothing was quoted or admitted.";

    /// <summary>True only for an explicit <c>Wtt:Enabled=true</c>; gates new quotes and admissions, never recovery of admitted work.</summary>
    public bool AdmissionEnabled => bool.TryParse(configuration?[EnabledKey], out var enabled) && enabled;

    public T? Resolve<T>(string chainType, ChainNetwork network) where T : class, IBlockchainProviderModule
    {
        if (string.IsNullOrWhiteSpace(chainType) || !Enum.IsDefined(network)
            || !providers.TryGetRailProvider(chainType, RailEnvironment.ForChain(network), out var rail)
            || rail is not IBlockchainProvider blockchain || blockchain.ActiveNetwork != network
            || !providers.TryGetModule<T>(blockchain, out var module) || module is null
            || !string.Equals(module.ChainType, chainType, StringComparison.Ordinal))
            return null;
        return module;
    }

    public static bool TryParseNetwork(string? value, out ChainNetwork network)
        => Enum.TryParse(value, false, out network) && Enum.IsDefined(network) && network.ToString() == value;
}
