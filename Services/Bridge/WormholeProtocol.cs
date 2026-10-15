using AZOA.WebAPI.Core.Blockchain.Bridge;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Bridge;

/// <summary>
/// The Wormhole cross-chain protocol: guardian-signed VAAs as the proof artifact, and one
/// lifecycle (observe → fetch → verify → settle) that is identical for every chain pair the
/// Guardians cover. This is the ONLY seam through which the bridge orchestrator can reach VAA
/// verification, and it is fail-closed at every step.
/// </summary>
/// <remarks>
/// <para>Guardian-quorum verification and the canonical replay digest live behind this one type.
/// Its <see cref="IWormholeAdapter"/> is the Guardian-API transport and the single hardened
/// verifier implementation (<c>Secp256k1VaaSignatureVerifier</c> is reached only from there, is
/// unconditional, and fails closed). Nothing else in the tree may verify a VAA.</para>
/// <para>See <c>Services/AGENTS.md</c> §bridge.</para>
/// </remarks>
public sealed class WormholeProtocol : IBridgeProtocol<WormholeVAA>
{
    private readonly IWormholeAdapter _adapter;
    private readonly ILogger<WormholeProtocol> _logger;

    public WormholeProtocol(IWormholeAdapter adapter, ILogger<WormholeProtocol> logger)
    {
        _adapter = adapter;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string ProtocolName => "Wormhole";

    /// <inheritdoc/>
    public BridgeMode Mode => BridgeMode.Wormhole;

    /// <inheritdoc/>
    public bool IsRouteSupported(string sourceChain, string targetChain)
        => _adapter.IsRouteSupported(sourceChain, targetChain);

    /// <inheritdoc/>
    public int? GetProtocolChainId(string azoaChainName)
        => _adapter.GetWormholeChainId(azoaChainName);

    /// <inheritdoc/>
    /// <remarks>
    /// Deriving an executable emitter sequence from a landed source transaction requires reading
    /// the Wormhole core-bridge message logs on that chain. No chain in this tree exposes that
    /// read yet, so observation refuses rather than inventing a sequence: a fabricated sequence
    /// would address a DIFFERENT transfer's VAA, and redeeming it would move somebody else's value.
    /// </remarks>
    public Task<AZOAResult<BridgeMessageHandle>> ObserveAsync(
        BridgeObservationRequest request, CancellationToken ct = default)
    {
        if (!IsRouteSupported(request.SourceChain, request.TargetChain))
            return Task.FromResult(Error<BridgeMessageHandle>(
                $"Wormhole route {request.SourceChain}→{request.TargetChain} is not supported"));

        return Task.FromResult(Error<BridgeMessageHandle>(
            $"Wormhole message observation is not implemented for {request.SourceChain}: the emitter "
            + "sequence must be read from the source chain's core-bridge message logs, and no such read "
            + "exists for this chain. Refusing to derive a sequence that would address a different "
            + "transfer's VAA."));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<WormholeVAA>> FetchProofAsync(
        BridgeMessageHandle handle, CancellationToken ct = default)
    {
        if (handle.EmitterChainId <= 0 || string.IsNullOrWhiteSpace(handle.EmitterAddress) || handle.Sequence < 0)
            return Error<WormholeVAA>("Missing Wormhole emitter information");

        return await _adapter.FetchVAAAsync(
            handle.EmitterChainId, handle.EmitterAddress, handle.Sequence, ct);
    }

    /// <inheritdoc/>
    public AZOAResult<BridgeProofFacts> DescribeProof(WormholeVAA proof)
    {
        // A VAA with no bytes or no digest is unusable proof — reject rather than persisting an
        // empty/null proof field that later steps would trust.
        if (string.IsNullOrWhiteSpace(proof.VaaBytes) || string.IsNullOrWhiteSpace(proof.Digest))
            return Error<BridgeProofFacts>(
                "VAA fetch returned incomplete proof (missing VAA bytes or digest) — refusing to persist");

        return Ok(new BridgeProofFacts(proof.VaaBytes, proof.SignatureCount, proof.Digest!));
    }

    /// <inheritdoc/>
    public AZOAResult<WormholeVAA> RehydrateProof(BridgeProofRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ProofBytes))
            return Error<WormholeVAA>("VAA bytes missing at redeem step");

        return Ok(new WormholeVAA
        {
            VaaBytes = record.ProofBytes,
            EmitterChainId = record.Handle.EmitterChainId,
            EmitterAddress = record.Handle.EmitterAddress,
            Sequence = record.Handle.Sequence,
            SignatureCount = record.SignatureCount,
            Version = 1,
        });
    }

    /// <inheritdoc/>
    public Task<AZOAResult<bool>> VerifyProofAsync(WormholeVAA proof, CancellationToken ct = default)
        => _adapter.VerifyVAAAsync(proof, ct);

    /// <inheritdoc/>
    /// <remarks>
    /// The canonical Wormhole replay key is <c>SHA256</c> over the BASE64-DECODED VAA bytes, so
    /// the consumed-proof ledger key collides with every other producer of the same VAA. Bytes
    /// that are not valid base64 have no derivable key: that is an outright rejection, never a
    /// fallback key, because a fallback would let the same VAA be consumed twice.
    /// </remarks>
    public AZOAResult<string> ComputeReplayDigest(BridgeProofRecord record)
    {
        try
        {
            return Ok(WormholeAdapter.ComputeVaaDigest(record.ProofBytes));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _logger.LogError(ex,
                "Wormhole replay digest could not be computed: VAA bytes are not valid base64; "
                + "refusing to settle an unverifiable VAA");
            return Error<string>(
                "VAA bytes are malformed (not valid base64) — redeem rejected, no mint performed", ex);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Settlement re-verifies the proof and then submits it to the target chain's token bridge.
    /// The caller must have already resolved <see cref="BridgeSettlementRequest{TProof}.TargetLeg"/>:
    /// a target chain with no <see cref="IBridgeTargetLeg"/> has no reviewed wrapped-asset
    /// lifecycle, and that absence is what stops settlement before any proof is consumed.
    /// </remarks>
    public async Task<AZOAResult<BridgeSettlement>> SettleAsync(
        BridgeSettlementRequest<WormholeVAA> request, CancellationToken ct = default)
    {
        var redeem = await _adapter.RedeemTransferAsync(
            request.TargetChain, request.Proof, request.RecipientAddress, ct);

        if (redeem.IsError || redeem.Result is null)
            return Error<BridgeSettlement>(redeem.Message, redeem.Exception);

        return Ok(
            new BridgeSettlement(redeem.Result.TxHash),
            redeem.Message);
    }

    private static AZOAResult<T> Ok<T>(T result, string message = "")
        => new() { IsError = false, Result = result, Message = message };

    private AZOAResult<T> Error<T>(string message, Exception? ex = null)
    {
        _logger.LogError(ex, "{Protocol} bridge error: {Message}", ProtocolName, message);
        return new AZOAResult<T> { IsError = true, Message = message, Exception = ex };
    }
}
