using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Providers.Blockchain.Base;

/// <summary>
/// The single place that turns a configured <c>Blockchain:DefaultNetwork</c> string into a
/// <see cref="ChainNetwork"/>.
/// </summary>
/// <remarks>
/// The distinction this type exists to enforce: an ABSENT setting means "nobody configured a
/// network", which is a local-dev shape and resolves to <see cref="ChainNetwork.Devnet"/>; a
/// PRESENT but unparseable setting means somebody configured a network and got it wrong, which
/// must never silently resolve to devnet. The old "parse failed ⇒ Devnet" fallback pinned a
/// production deployment to devnet on a typo — value would then be locked, minted, and released
/// against the wrong network with no error anywhere.
/// </remarks>
public static class ChainNetworkResolution
{
    /// <summary>Config key every caller resolves the platform network from.</summary>
    public const string DefaultNetworkConfigPath = "Blockchain:DefaultNetwork";

    /// <summary>
    /// Resolves the configured network. Absent ⇒ <see cref="ChainNetwork.Devnet"/>.
    /// Present but unparseable or undefined ⇒ throws, naming the offending value.
    /// </summary>
    /// <exception cref="InvalidOperationException">The setting is present but not a valid network.</exception>
    public static ChainNetwork FromConfiguration(IConfiguration configuration)
        => FromConfiguredName(configuration.GetValue<string>(DefaultNetworkConfigPath));

    /// <inheritdoc cref="FromConfiguration(IConfiguration)"/>
    public static ChainNetwork FromConfiguredName(string? configuredName)
    {
        if (string.IsNullOrWhiteSpace(configuredName))
            return ChainNetwork.Devnet;

        if (Enum.TryParse<ChainNetwork>(configuredName.Trim(), ignoreCase: true, out var network)
            && Enum.IsDefined(network))
        {
            return network;
        }

        throw new InvalidOperationException(
            $"{DefaultNetworkConfigPath} is set to '{configuredName}', which is not a supported network. "
            + $"Valid values are {string.Join(", ", Enum.GetNames<ChainNetwork>())}. Refusing to fall back "
            + "to Devnet: a misconfigured network would move real value on the wrong chain.");
    }
}
