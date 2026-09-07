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

    /// <summary>
    /// D19 soulbound revocation: moves <paramref name="amount"/> of an ASA OUT of
    /// <paramref name="holderAddress"/> without the holder's signature, using the
    /// asset's clawback authority. This is the only primitive in the codebase that
    /// takes value from an account that did not authorize the transaction, so the
    /// implementation must PROVE, against the node and before signing, that (a) the
    /// asset exists, (b) its on-chain clawback address is exactly the account that
    /// will sign, (c) the holder actually holds at least that amount, and (d) the
    /// destination can receive it. Anything unproven is a refusal, not an attempt.
    /// </summary>
    /// <param name="assetId">Numeric ASA id of the credential.</param>
    /// <param name="holderAddress">Account the asset is taken FROM (the ASA's <c>AssetSender</c>).</param>
    /// <param name="recipientAddress">Account the asset is returned TO (normally the issuer).</param>
    /// <param name="amount">Units to claw back, in ASA base units.</param>
    /// <param name="signingContext">Signing context for the clawback authority (platform for soulbound mints).</param>
    /// <param name="clawbackAddress">
    /// The clawback authority that signs. Null resolves the configured platform address —
    /// the address <c>CreateSoulboundAsaAsync</c> stamps on every soulbound mint.
    /// </param>
    /// <returns>
    /// The confirmed transaction id. A broadcast that did not confirm within the poll
    /// window returns a SUCCESS result whose message carries
    /// <c>OperationStatus.PendingConfirmationMarker</c> — callers must treat that as
    /// "submitted, outcome unknown", never as a completed revocation.
    /// </returns>
    Task<AZOAResult<string>> ClawbackAssetAsync(
        string assetId,
        string holderAddress,
        string recipientAddress,
        AZOA.WebAPI.Core.RailAmount amount,
        AZOA.WebAPI.Core.SigningContext signingContext,
        string? clawbackAddress = null,
        CancellationToken ct = default);
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

    /// <summary>
    /// Reads exact chain evidence reconstructed from a durable accepted-group receipt.
    /// The evidence deliberately excludes signing credentials and cannot authorize a
    /// submission; implementations must treat it as observation-only.
    /// </summary>
    Task<AZOAResult<AtomicTransferGroupObservation>> ObserveAtomicTransferGroupAsync(
        AtomicTransferGroupObservationEvidence evidence,
        CancellationToken ct = default);
}
