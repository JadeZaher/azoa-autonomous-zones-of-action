using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

/// <summary>
/// Extension module pattern for chain-specific features that are not part of the standard cross-chain interface.
/// Each module is keyed by a capability name (e.g., "Algorand.ASA", "Solana.Metaplex", "Ethereum.ERC721").
/// The base provider exposes a generic module resolution method; consumers cast to the specific module interface.
/// </summary>
public interface IBlockchainProviderModule
{
    string CapabilityName { get; }
    string ChainType { get; }
}

// Example chain-specific capability interfaces — consumers check for presence via IBlockchainProvider.TryGetModule<T>()

public interface IAlgorandASAModule : IBlockchainProviderModule
{
    Task<AZOAResult<string>> CreateASAAsync(
        string name, string unitName, int total, int decimals,
        string managerAddress, string reserveAddress, string freezeAddress, string clawbackAddress,
        string walletAddress, CancellationToken ct = default);

    /// <summary>
    /// tenant-consent-delegation AC4b: ASA-create with an explicit
    /// <see cref="SigningContext"/> so a tenant-DRIVEN fungible-token launch (which
    /// is platform-SIGNED) carries the acting tenant + scope to the custody seam for
    /// the live consent check. Pass <see cref="SigningContext.Platform"/> for an
    /// ungated platform-internal create.
    /// </summary>
    Task<AZOAResult<string>> CreateASAAsync(
        string name, string unitName, int total, int decimals,
        string managerAddress, string reserveAddress, string freezeAddress, string clawbackAddress,
        string walletAddress, SigningContext signingContext, CancellationToken ct = default);

    Task<AZOAResult<bool>> OptInAsync(string assetId, string walletAddress, CancellationToken ct = default);
    Task<AZOAResult<string>> GetAssetHoldingAsync(string assetId, string address, CancellationToken ct = default);
}

public interface ISolanaMetaplexModule : IBlockchainProviderModule
{
    Task<AZOAResult<string>> CreateMetadataAccountAsync(
        string mint, string name, string symbol, string uri,
        int sellerFeeBasisPoints, string walletAddress, CancellationToken ct = default);

    Task<AZOAResult<bool>> UpdateMetadataAsync(
        string mint, string? newUri, string? newName, string walletAddress, CancellationToken ct = default);
}

public interface ISolanaSPLModule : IBlockchainProviderModule
{
    Task<AZOAResult<string>> CreateTokenAccountAsync(string mint, string owner, CancellationToken ct = default);
    Task<AZOAResult<string>> CloseTokenAccountAsync(string tokenAccount, string owner, CancellationToken ct = default);
}

/// <summary>Optional capability for chains that can accept both transfer legs atomically.</summary>
public interface IAtomicTransferGroupModule : IBlockchainProviderModule
{
    /// <summary>True only when this bound adapter can construct, sign, and submit a real atomic group.</summary>
    bool SupportsAtomicTransferGroups { get; }

    /// <summary>
    /// Submits the primary and treasury legs as one chain-atomic group. The request
    /// must have been constructed from this resolved provider's canonical binding;
    /// an error means no group acceptance was established. Implementations must never
    /// fall back to sequential individual transfers or return an acceptance detached
    /// from the request's group identity.
    /// </summary>
    Task<AZOAResult<AtomicTransferGroupSubmission>> SubmitAtomicTransferGroupAsync(
        AtomicTransferGroupRequest request,
        CancellationToken ct = default);
}

/// <summary>Optional read-only capability for classifying the two on-chain legs of an accepted atomic group.</summary>
public interface IAtomicTransferGroupObservationModule : IBlockchainProviderModule
{
    /// <summary>
    /// Reads and validates both legs against the immutable request and accepted submission.
    /// This is evidence collection only: it must not persist, sign, submit, or terminalize a settlement.
    /// Caller cancellation propagates; unavailable network evidence is reported as an observation verdict.
    /// </summary>
    Task<AZOAResult<AtomicTransferGroupObservation>> ObserveAtomicTransferGroupAsync(
        AtomicTransferGroupObservationRequest request,
        CancellationToken ct = default);
}
