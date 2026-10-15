// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Core;

/// <summary>Whether a rail environment moves real value or rehearsal value.</summary>
public enum RailEnvironmentKind
{
    /// <summary>Nothing settled here is worth anything: a devnet, a testnet, a payment-processor sandbox.</summary>
    Test = 0,

    /// <summary>Real value. A chain mainnet, or a payment processor's live mode.</summary>
    Live = 1,
}

/// <summary>
/// Which environment of a value rail a provider instance is bound to (decision D9).
/// See <c>Core/AGENTS.md</c> §rail-environment.
/// </summary>
/// <remarks>
/// <see cref="ChainNetwork"/> is chain-shaped — Devnet / Testnet / Mainnet. A fiat rail
/// has two environments (test and live) and no third, so a fiat provider cannot be
/// asked for "Devnet" without inventing an answer. This type carries the only
/// distinction every rail actually shares — is the value real — plus the rail's own
/// name for the environment, and converts to and from <see cref="ChainNetwork"/> only
/// where a chain is genuinely on the other side.
/// </remarks>
public readonly record struct RailEnvironment
{
    private readonly string? _name;

    private RailEnvironment(RailEnvironmentKind kind, string? name)
    {
        Kind = kind;
        _name = name;
    }

    /// <summary>Whether value settled in this environment is real.</summary>
    public RailEnvironmentKind Kind { get; }

    /// <summary>
    /// The rail's own name for this environment — <c>"Devnet"</c>/<c>"Testnet"</c>/
    /// <c>"Mainnet"</c> for a chain, <c>"test"</c>/<c>"live"</c> for a payment rail.
    /// </summary>
    public string Name => _name ?? (Kind == RailEnvironmentKind.Live ? LiveName : TestName);

    private const string TestName = "test";
    private const string LiveName = "live";

    /// <summary>The rail's generic rehearsal environment.</summary>
    public static RailEnvironment Test { get; } = new(RailEnvironmentKind.Test, null);

    /// <summary>The rail's generic real-value environment.</summary>
    public static RailEnvironment Live { get; } = new(RailEnvironmentKind.Live, null);

    /// <summary>
    /// The rail environment a chain network denotes. Only <see cref="ChainNetwork.Mainnet"/>
    /// carries real value; devnet and testnet are both rehearsal, and stay distinguishable
    /// through <see cref="Name"/>.
    /// </summary>
    public static RailEnvironment ForChain(ChainNetwork network)
    {
        if (!Enum.IsDefined(network))
            throw new ArgumentOutOfRangeException(nameof(network), network, "Unsupported blockchain network.");

        return new(
            network == ChainNetwork.Mainnet ? RailEnvironmentKind.Live : RailEnvironmentKind.Test,
            network.ToString());
    }

    /// <summary>
    /// A rail-specific environment under an explicit real/rehearsal classification —
    /// how a non-chain rail names its own environments.
    /// </summary>
    public static RailEnvironment Named(RailEnvironmentKind kind, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(kind, name.Trim());
    }

    /// <summary>
    /// The <see cref="ChainNetwork"/> this environment denotes, when it denotes one at
    /// all. False for any environment whose name is not a chain network — which is the
    /// whole point: a fiat rail's "live" must not silently become "Mainnet".
    /// </summary>
    public bool TryToChainNetwork(out ChainNetwork network) =>
        Enum.TryParse(_name, ignoreCase: true, out network) && Enum.IsDefined(network);

    public override string ToString() => Name;
}

/// <summary>
/// What a factory hands a provider to bind it to one rail environment (decision D9).
/// </summary>
/// <param name="Environment">The environment this instance settles in.</param>
/// <param name="Endpoint">Connection settings for that environment. Named for the
/// chain era but rail-generic in content — a base URL, an API key/token, timeout and
/// retry budgets, and whether the environment is enabled at all — which is exactly
/// what a payment-processor rail needs too. Renaming the type is a follow-up; its
/// shape is not the reason a fiat rail could not be registered.</param>
public readonly record struct ValueRailBinding(
    RailEnvironment Environment,
    BlockchainNetworkConfig Endpoint);
