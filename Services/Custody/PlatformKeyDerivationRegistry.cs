using System.Diagnostics.CodeAnalysis;

namespace AZOA.WebAPI.Services.Custody;

/// <summary>
/// Chain → <see cref="IPlatformKeyDerivation"/> lookup behind the platform signing door.
/// Total by construction: a chain with no entry resolves to nothing, so the custody
/// service fails closed naming that chain instead of reaching for another chain's rule.
/// See <c>Services/Custody/AGENTS.md</c> §platform-key-derivation.
/// </summary>
public sealed class PlatformKeyDerivationRegistry
{
    /// <summary>
    /// The registry the production custody service uses. Stateless and immutable, so it is
    /// a static default rather than a DI registration — wiring it through Program.cs would
    /// buy nothing and risk a captive dependency in the app-lifetime provider cache.
    /// </summary>
    public static PlatformKeyDerivationRegistry Default { get; } = new(
    [
        new AlgorandPlatformKeyDerivation(),
        new SolanaPlatformKeyDerivation(),
        new EthereumPlatformKeyDerivation(),
    ]);

    private readonly Dictionary<string, IPlatformKeyDerivation> _byChainType;

    public PlatformKeyDerivationRegistry(IEnumerable<IPlatformKeyDerivation> derivations)
    {
        ArgumentNullException.ThrowIfNull(derivations);

        _byChainType = new Dictionary<string, IPlatformKeyDerivation>(StringComparer.OrdinalIgnoreCase);
        foreach (var derivation in derivations)
        {
            ArgumentNullException.ThrowIfNull(derivation);
            if (string.IsNullOrWhiteSpace(derivation.ChainType))
                throw new ArgumentException("A platform key derivation must name its chain.", nameof(derivations));
            if (derivation.KeyLength <= 0)
                throw new ArgumentException(
                    $"Platform key derivation for '{derivation.ChainType}' must declare a positive key length.",
                    nameof(derivations));
            if (string.IsNullOrWhiteSpace(derivation.PlatformKeyConfigPath))
                throw new ArgumentException(
                    $"Platform key derivation for '{derivation.ChainType}' must name its config path.",
                    nameof(derivations));

            // Mirrors BlockchainProviderFactory: two rules for one chain is a wiring bug,
            // and silently keeping one of them is how the wrong key gets derived.
            if (!_byChainType.TryAdd(derivation.ChainType.Trim(), derivation))
                throw new InvalidOperationException(
                    $"Duplicate platform key derivation registered for chain '{derivation.ChainType}'.");
        }

        RegisteredChainTypes = _byChainType.Values.Select(item => item.ChainType).OrderBy(item => item).ToArray();
    }

    /// <summary>Chains with an explicit derivation, sorted. Names only — never config values.</summary>
    public IReadOnlyList<string> RegisteredChainTypes { get; }

    /// <summary>Comma-separated <see cref="RegisteredChainTypes"/> for error messages.</summary>
    public string RegisteredChainTypesDisplay =>
        RegisteredChainTypes.Count == 0 ? "(none)" : string.Join(", ", RegisteredChainTypes);

    /// <summary>Resolves a chain's derivation. Returns false for null, blank, or unregistered chains.</summary>
    public bool TryGet(string? chainType, [NotNullWhen(true)] out IPlatformKeyDerivation? derivation)
    {
        derivation = null;
        if (string.IsNullOrWhiteSpace(chainType))
            return false;

        return _byChainType.TryGetValue(chainType.Trim(), out derivation);
    }
}
