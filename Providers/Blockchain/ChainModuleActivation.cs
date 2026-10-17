using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Providers.Blockchain;

/// <summary>
/// D-7 per-network activation pins for reviewed-but-gated capability modules: a declared DEX or exchange module is
/// advertised only where <c>{Module}:Activation:{Chain}:{Network}</c> is <c>true</c>. See <c>Providers/Blockchain/AGENTS.md</c>
/// §module activation.
/// </summary>
public static class ChainModuleActivation
{
    public const string Dex = "Dex";
    public const string Exchange = "Exchange";

    /// <summary>Configuration key of one pin.</summary>
    public static string Key(string module, string chainType, ChainNetwork network) => $"{module}:Activation:{chainType}:{network}";

    /// <summary>True only for an explicit <c>true</c>; absent, blank or malformed pins keep the module unadvertised.</summary>
    public static bool IsActive(IConfiguration config, string module, string chainType, ChainNetwork network)
        => bool.TryParse(config[Key(module, chainType, network)], out var active) && active;

    /// <summary>The provider's module of type <typeparamref name="T"/> when it implements it AND, for a gated module, its pin is on.</summary>
    public static bool TryGetGated<T>(IBlockchainProvider provider, IConfiguration config, out T? module) where T : class
    {
        var gate = typeof(T) == typeof(IDexTransactionModule) ? Dex
            : typeof(T) == typeof(IExchangeTransactionModule) ? Exchange
            : null;
        module = provider as T;
        if (module is null || gate is null || IsActive(config, gate, provider.ChainType, provider.ActiveNetwork)) return module is not null;
        module = null;
        return false;
    }
}
