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

    /// <summary>Acknowledgement a deployed (Railway) host must also carry before any activation pin counts.</summary>
    public const string DeployedHostAcknowledgementKey = "AZOA:Activation:DeployedHostAcknowledged";

    /// <summary>True only for an explicit <c>true</c>; absent, blank or malformed pins keep the module unadvertised.</summary>
    public static bool IsActive(IConfiguration config, string module, string chainType, ChainNetwork network)
        => bool.TryParse(config[Key(module, chainType, network)], out var active) && active && HostHonoursPins(config);

    /// <summary>
    /// False on a Railway-deployed host (platform-injected <c>RAILWAY_*</c> ids) without <see cref="DeployedHostAcknowledgementKey"/>,
    /// so a deployment mistakenly run as Development never inherits the development pins. See Providers/Blockchain/AGENTS.md §module activation.
    /// </summary>
    public static bool HostHonoursPins(IConfiguration config)
        => string.IsNullOrEmpty(config["RAILWAY_PROJECT_ID"]) && string.IsNullOrEmpty(config["RAILWAY_ENVIRONMENT_ID"])
            || bool.TryParse(config[DeployedHostAcknowledgementKey], out var acknowledged) && acknowledged;

    /// <summary>The provider's module regardless of its pin; only for work already admitted while the pin was on (RV-L5-11).</summary>
    public static bool TryGetUngated<T>(IBlockchainProvider provider, out T? module) where T : class
    {
        module = provider as T;
        return module is not null;
    }

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
