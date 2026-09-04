using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Core.Blockchain.Bridge;

/// <summary>Request to lock source-chain value into a platform-controlled bridge vault.</summary>
public sealed record BridgeLockRequest(
    string TokenId,
    string VaultAddress,
    RailAmount Amount,
    string TargetChain,
    string TargetRecipient);

/// <summary>Outcome of a source lock: the real chain transaction id and whether it is still nonterminal.</summary>
/// <remarks>
/// <paramref name="PendingConfirmation"/> is an explicit field rather than a message
/// marker: a lock that is broadcast but unconfirmed must never be read as settled, and
/// a bool the orchestrator reads directly cannot be lost to a reworded message.
/// </remarks>
public sealed record BridgeLock(
    string TransactionId,
    bool PendingConfirmation,
    string Detail);

/// <summary>Request to release previously locked value from the bridge vault back to a recipient.</summary>
public sealed record BridgeReleaseRequest(
    string TokenId,
    string VaultAddress,
    RailAmount Amount,
    string RecipientAddress);

/// <summary>Request to mint the target-chain wrapped representation of a source asset.</summary>
public sealed record BridgeMintRequest(
    string SourceChain,
    string SourceTokenId,
    string TokenUri,
    RailAmount Amount,
    string RecipientAddress);

/// <summary>Request to burn a target-chain wrapped asset so the source original can be released.</summary>
public sealed record BridgeBurnRequest(
    string TokenId,
    RailAmount Amount,
    string SourceChain,
    string SourceRecipient,
    string WalletAddress);

/// <summary>
/// Durable identity of one cross-chain message: which emitter published it and at what
/// sequence. Persisted on the bridge row and used to fetch the protocol's proof later.
/// </summary>
public sealed record BridgeMessageHandle(
    int EmitterChainId,
    string EmitterAddress,
    long Sequence);

/// <summary>Everything a protocol needs to derive the message handle for a landed source lock.</summary>
public sealed record BridgeObservationRequest(
    string SourceChain,
    string TargetChain,
    string SourceTransactionId,
    string TokenId,
    string RecipientAddress);

/// <summary>
/// The persisted shape of a proof — the serialized artifact plus the message handle it
/// belongs to. The orchestrator stores exactly this and rehydrates a typed proof from it,
/// so no protocol-specific field ever has to be read out of the row by the orchestrator.
/// </summary>
public sealed record BridgeProofRecord(
    string ProofBytes,
    int SignatureCount,
    BridgeMessageHandle Handle);

/// <summary>Protocol-agnostic facts about a freshly fetched proof, ready for persistence.</summary>
public sealed record BridgeProofFacts(
    string ProofBytes,
    int SignatureCount,
    string CanonicalDigest);

/// <summary>Request to settle a verified proof on the target chain through its resolved leg.</summary>
public sealed record BridgeSettlementRequest<TProof>(
    string TargetChain,
    TProof Proof,
    string RecipientAddress,
    IBridgeTargetLeg TargetLeg) where TProof : class;

/// <summary>Outcome of a protocol settlement: the real target-chain transaction id.</summary>
public sealed record BridgeSettlement(string TransactionId);

/// <summary>
/// A cross-chain bridge protocol, generic over the proof artifact it produces.
/// <para>The non-generic root exists so an orchestrator can name and route a protocol
/// (mode, supported routes, protocol chain ids) without knowing its proof type.</para>
/// </summary>
public interface IBridgeProtocol
{
    /// <summary>Human-readable protocol name, used in operator-facing messages.</summary>
    string ProtocolName { get; }

    /// <summary>The bridge mode this protocol implements.</summary>
    BridgeMode Mode { get; }

    /// <summary>True when this protocol can carry a message between the two chains.</summary>
    bool IsRouteSupported(string sourceChain, string targetChain);

    /// <summary>The protocol's own id for an AZOA chain name, or null when unmapped.</summary>
    int? GetProtocolChainId(string azoaChainName);
}

/// <summary>
/// The proof-bearing half of a bridge protocol. The lifecycle varies by PROTOCOL, not by
/// chain pair — Algorand→Solana and Ethereum→Solana over Wormhole share one proof type and
/// one state machine — and the protocol is chosen at composition time, so
/// <typeparamref name="TProof"/> binds statically and the generic earns its keep.
/// </summary>
public interface IBridgeProtocol<TProof> : IBridgeProtocol where TProof : class
{
    /// <summary>
    /// Derive the durable message handle for a source lock that has already landed.
    /// Observation is read-only: it must not sign, submit, or move value.
    /// </summary>
    Task<AZOAResult<BridgeMessageHandle>> ObserveAsync(
        BridgeObservationRequest request, CancellationToken ct = default);

    /// <summary>Fetch the protocol's signed proof artifact for a previously observed handle.</summary>
    Task<AZOAResult<TProof>> FetchProofAsync(
        BridgeMessageHandle handle, CancellationToken ct = default);

    /// <summary>Project a freshly fetched proof into the facts the bridge row persists.</summary>
    AZOAResult<BridgeProofFacts> DescribeProof(TProof proof);

    /// <summary>Rebuild a typed proof from the persisted record. Never trusts absent fields.</summary>
    AZOAResult<TProof> RehydrateProof(BridgeProofRecord record);

    /// <summary>
    /// Verify the proof. FAIL CLOSED: any structural, quorum, or cryptographic doubt is an
    /// error, never a permissive true.
    /// </summary>
    Task<AZOAResult<bool>> VerifyProofAsync(TProof proof, CancellationToken ct = default);

    /// <summary>
    /// The single canonical replay-ledger key for a persisted proof. Every producer of the
    /// same proof MUST derive the same digest or the consumed-proof ledger cannot collide.
    /// An underivable digest is an error — never a fallback key.
    /// </summary>
    AZOAResult<string> ComputeReplayDigest(BridgeProofRecord record);

    /// <summary>
    /// Settle a verified proof on the target chain. The caller must have already resolved
    /// the target leg: a chain with no <see cref="IBridgeTargetLeg"/> cannot be settled on,
    /// and that absence is the fail-closed gate.
    /// </summary>
    Task<AZOAResult<BridgeSettlement>> SettleAsync(
        BridgeSettlementRequest<TProof> request, CancellationToken ct = default);
}
