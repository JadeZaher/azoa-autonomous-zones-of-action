using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.ClientSigning;

/// <summary>
/// Client-signed value transfers (D-3): the server saves the exact unsigned bytes, the wallet owner signs them
/// off-platform, and only a byte-identical signature over that plan is ever broadcast.
/// See <c>Providers/Blockchain/AGENTS.md</c> §client-signed transfers.
/// </summary>
public interface IClientSignedTransferModule : IBlockchainProviderModule
{
    /// <summary>Builds and returns the exact unsigned transaction the external signer must sign. Never signs or sends.</summary>
    Task<AZOAResult<ClientSigningPlan>> PlanClientSignedTransferAsync(ValueTransferIntent intent, CancellationToken ct);

    /// <summary>Accepts only the plan's exact bytes signed by the plan signer; returns the chain transaction id.</summary>
    AZOAResult<string> VerifyClientSignedTransfer(ClientSigningPlan plan, string signedTransactionBase64);

    /// <summary>Broadcasts verified bytes (identical bytes are idempotent); the caller has already persisted them.</summary>
    Task<AZOAResult<string>> SubmitClientSignedTransferAsync(ClientSigningPlan plan, string signedTransactionBase64, CancellationToken ct);

    /// <summary>
    /// Re-observes the plan on chain. <paramref name="transactionId"/> is the submitted (or operator-supplied) id; when it
    /// is null the chain is searched for the plan's own transaction where the chain allows it.
    /// </summary>
    Task<AZOAResult<ClientSigningObservation>> ObserveClientSignedTransferAsync(ClientSigningPlan plan, string? transactionId,
        CancellationToken ct);
}

/// <summary>The saved unsigned plan; <see cref="UnsignedPayloadBase64"/> is exactly what the signer signs.</summary>
/// <param name="Format"><c>algorand-txn-msgpack/1</c>, <c>ethereum-eip1559-unsigned/1</c> or <c>solana-message-legacy/1</c>.</param>
/// <param name="PredictedTransactionId">Algorand only: the id is a hash of the unsigned bytes.</param>
/// <param name="ValidThroughHeight">Algorand last-valid round or Solana last-valid block height; null when it never expires (Ethereum).</param>
/// <param name="Nonce">Ethereum account nonce the plan consumes.</param>
/// <param name="RecoveryJson">Chain-private recovery metadata (Solana durable plan).</param>
public sealed record ClientSigningPlan(int Version, string ChainType, string Network, string ProtocolIdentity, string Format,
    string Signer, string UnsignedPayloadBase64, string IntentHash, string? PredictedTransactionId = null,
    long? ValidThroughHeight = null, string? Nonce = null, string? RecoveryJson = null);

/// <summary>One chain re-observation of a client-signed plan.</summary>
public sealed record ClientSigningObservation(string Verdict, string? TransactionId = null, string? Detail = null);

/// <summary>Closed set of <see cref="ClientSigningObservation.Verdict"/> values.</summary>
public static class ClientSigningVerdicts
{
    /// <summary>The plan's own transaction is finalized and succeeded.</summary>
    public const string Confirmed = "Confirmed";
    /// <summary>The plan's own transaction is finalized and failed (fee spent, no transfer).</summary>
    public const string FailedOnChain = "FailedOnChain";
    /// <summary>The validity window passed and the chain proves the plan never landed; it never can.</summary>
    public const string Expired = "Expired";
    /// <summary>Ethereum: the plan nonce is finalized and the given transaction is not it. <see cref="ClientSigningObservation.TransactionId"/>
    /// names the consumer when a found, different transaction holds the nonce; null means the consumer is unattributed.</summary>
    public const string NonceConsumed = "NonceConsumed";
    /// <summary>Nothing terminal is provable yet.</summary>
    public const string Pending = "Pending";
}

/// <summary>Plan payload formats.</summary>
public static class ClientSigningFormats
{
    public const string Algorand = "algorand-txn-msgpack/1";
    public const string Ethereum = "ethereum-eip1559-unsigned/1";
    public const string Solana = "solana-message-legacy/1";
}

/// <summary>Result codes the client-signing modules and service return.</summary>
public static class ClientSigningCodes
{
    /// <summary>The plan's validity window has passed.</summary>
    public const string Expired = "CLIENT_SIGNATURE_EXPIRED";
    /// <summary>The broadcast outcome is unknown; the saved signed bytes are reconciled from chain truth.</summary>
    public const string BroadcastUnconfirmed = "CLIENT_SIGNATURE_BROADCAST_UNCONFIRMED";
    /// <summary>The submitted bytes are not the saved plan signed by its signer.</summary>
    public const string SignatureMismatch = "CLIENT_SIGNATURE_MISMATCH";
}
