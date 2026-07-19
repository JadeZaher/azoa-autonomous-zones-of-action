using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

/// <summary>
/// Adapter for the Wormhole cross-chain messaging protocol.
/// Handles VAA fetching, verification, and bridging lifecycle.
/// </summary>
public interface IWormholeAdapter
{
    /// <summary>
    /// Initiate a Wormhole transfer on the source chain.
    /// Publishes a message through the Wormhole Core Bridge and returns
    /// the emitter/sequence info needed to fetch the VAA.
    /// </summary>
    Task<AZOAResult<WormholeTransferInitiation>> InitiateTransferAsync(
        string sourceChain, string targetChain,
        string tokenId, string senderAddress, string recipientAddress,
        ulong amount, CancellationToken ct = default);

    /// <summary>
    /// Poll the Guardian network for a signed VAA matching the given emitter/sequence.
    /// Retries until the VAA is available or timeout is reached.
    /// </summary>
    Task<AZOAResult<WormholeVAA>> FetchVAAAsync(
        int emitterChainId, string emitterAddress, long sequence,
        CancellationToken ct = default);

    /// <summary>
    /// Verify a VAA's Guardian signatures and payload integrity.
    /// Returns true if the VAA has sufficient signatures and valid structure.
    /// </summary>
    Task<AZOAResult<bool>> VerifyVAAAsync(WormholeVAA vaa, CancellationToken ct = default);

    /// <summary>
    /// Redeem (complete) a Wormhole transfer on the target chain using a verified VAA.
    /// This submits the VAA to the target chain's Token Bridge for minting/releasing.
    /// </summary>
    Task<AZOAResult<WormholeRedemptionResult>> RedeemTransferAsync(
        string targetChain, WormholeVAA vaa, string recipientAddress,
        CancellationToken ct = default);

    /// <summary>
    /// Get the Wormhole chain ID for an AZOA chain name.
    /// </summary>
    int? GetWormholeChainId(string azoaChainName);

    /// <summary>
    /// Check whether a route between two chains is supported via Wormhole.
    /// </summary>
    bool IsRouteSupported(string sourceChain, string targetChain);
}
