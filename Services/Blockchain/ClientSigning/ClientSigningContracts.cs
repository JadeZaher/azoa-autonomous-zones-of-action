using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Providers.Blockchain.ClientSigning;

namespace AZOA.WebAPI.Services.Blockchain.ClientSigning;

/// <summary>Parameter-bag keys a client-signed operation row carries.</summary>
public static class ClientSigningParameters
{
    /// <summary>The saved <see cref="ClientSigningPlan"/> (JSON).</summary>
    public const string Plan = "ClientSigningPlan";
    /// <summary>The submitted signed bytes, persisted before the first broadcast.</summary>
    public const string SignedTransaction = "ClientSignedTransaction";
    /// <summary>The manager's derived idempotency key, settled when the row reaches a terminal state.</summary>
    public const string ExecutionIdempotencyKey = "ExecutionIdempotencyKey";
    /// <summary>Why a terminal failure has no chain effect: <c>Abandoned</c>, <c>Superseded</c> or <c>FailedOnChain</c>.</summary>
    public const string Outcome = "ClientSigningOutcome";
    /// <summary>Resumable chain absence-scan progress (Solana finalized-ancestry cursor), carried between reconcile passes.</summary>
    public const string AbsenceEvidence = "ClientSigningAbsenceEvidence";
    /// <summary>Audit of an operator resolution (JSON).</summary>
    public const string OperatorResolution = "OperatorResolution";
    /// <summary>Chain transaction id (shared with every other operation row).</summary>
    public const string TransactionHash = "TxHash";
    /// <summary>Human-readable failure detail (shared with every other operation row).</summary>
    public const string Error = "Error";
}

/// <summary>Body of <c>POST /api/blockchain/operations/{id}/submit-signed</c>.</summary>
public sealed record ClientSignedSubmission(string SignedTransactionBase64);

/// <summary>Body of <c>POST /api/operator/blockchain-operations/{id}/resolve</c>; the outcome is always re-proven from chain state.</summary>
public sealed record ClientSigningResolution(string Reason, string? TransactionId = null);

/// <summary>What the external signer needs: the exact bytes to sign and how the chain will judge them.</summary>
public sealed record ClientSigningPlanView(Guid OperationId, string Status, string ChainType, string Network, string ProtocolIdentity,
    string Format, string Signer, string UnsignedPayloadBase64, string? PredictedTransactionId, long? ValidThroughHeight, string? Nonce)
{
    /// <summary>Projects a plan without any recovery metadata.</summary>
    public static ClientSigningPlanView From(IBlockchainOperation operation, ClientSigningPlan plan)
        => new(operation.Id, operation.Status, plan.ChainType, plan.Network, plan.ProtocolIdentity, plan.Format, plan.Signer,
            plan.UnsignedPayloadBase64, plan.PredictedTransactionId, plan.ValidThroughHeight, plan.Nonce);
}

/// <summary>Operator projection of a stuck client-signed operation.</summary>
public sealed record StuckClientSignedOperationView(Guid OperationId, Guid? UserId, string Status, DateTime CreatedDate,
    string ChainType, string Network, string Signer, string? TransactionId, string? Nonce, long? ValidThroughHeight, bool Submitted, string? Detail);
