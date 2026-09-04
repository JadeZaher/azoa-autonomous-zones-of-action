using AZOA.WebAPI.Core.Blockchain.Bridge;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

/// <summary>
/// Capability module: this chain can act as the SOURCE leg of a bridge — lock value into a
/// platform-controlled vault, and release it back out again.
/// <para>
/// A chain that cannot do this simply does not implement the module. Absence IS the
/// fail-closed refusal, structurally: there is no boolean to set true by mistake and no
/// refusal body that can regress into an <c>Ok</c>. See <c>Providers/Blockchain/AGENTS.md</c>
/// §bridge.
/// </para>
/// </summary>
public interface IBridgeSourceLeg : IBlockchainProviderModule
{
    /// <summary>
    /// Lock an asset into the bridge vault on this chain. MUST broadcast a real transaction
    /// and return its real id, or fail loud. A submitted-but-unconfirmed lock returns
    /// <see cref="BridgeLock.PendingConfirmation"/> = true and must be reconciled by hash.
    /// </summary>
    Task<AZOAResult<BridgeLock>> LockAsync(BridgeLockRequest request, CancellationToken ct = default);

    /// <summary>
    /// Release previously locked source value from the platform-controlled vault after the
    /// paired target burn is positively confirmed. A success without
    /// <see cref="Models.OperationStatus.PendingConfirmationMarker"/> means the release is
    /// chain-confirmed; a marked success is submitted but nonterminal.
    /// </summary>
    Task<AZOAResult<string>> ReleaseAsync(BridgeReleaseRequest request, CancellationToken ct = default);
}

/// <summary>
/// Capability module: this chain can act as the TARGET leg of a bridge — mint the wrapped
/// representation of a source asset, and burn it again to release the original.
/// <para>Absence is the fail-closed refusal; see <see cref="IBridgeSourceLeg"/>.</para>
/// </summary>
public interface IBridgeTargetLeg : IBlockchainProviderModule
{
    /// <summary>
    /// Mint a wrapped representation of an asset bridged from another chain. MUST broadcast a
    /// real transaction and return its real id, or fail loud.
    /// </summary>
    Task<AZOAResult<string>> MintWrappedAsync(BridgeMintRequest request, CancellationToken ct = default);

    /// <summary>
    /// Burn a wrapped asset so the original can be released on the source chain. MUST prove
    /// the supply was actually destroyed, or fail loud.
    /// </summary>
    Task<AZOAResult<string>> BurnWrappedAsync(BridgeBurnRequest request, CancellationToken ct = default);
}
