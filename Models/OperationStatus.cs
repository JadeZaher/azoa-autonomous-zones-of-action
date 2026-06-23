namespace AZOA.WebAPI.Models;

/// <summary>
/// The closed set of <see cref="BlockchainOperation.Status"/> values. Every
/// producer (<see cref="AZOA.WebAPI.Managers.BlockchainOperationManager"/>)
/// and consumer (<see cref="AZOA.WebAPI.Services.Reconciliation.ReconciliationService"/>)
/// references these constants instead of bare string literals, so a typo or a
/// new state is a compile-time concern, not a silent runtime divergence.
///
/// <para>Kept as <c>const string</c> (not an <c>enum</c>) on purpose:
/// <see cref="AZOA.WebAPI.Interfaces.IBlockchainOperation.Status"/> is a
/// public string contract and <c>BlockchainOperationBuilder.WithStatus</c>
/// accepts free-form values — an enum would break that contract and force
/// churn across the interface, builder, and both storage providers. The column
/// stays a human-readable string (mapped <c>HasMaxLength(64)</c>); these
/// constants kill the divergence risk without any schema or type change.</para>
/// </summary>
public static class OperationStatus
{
    // ─── Lifecycle states ───

    /// <summary>Initial state; not yet executed.</summary>
    public const string Pending = "Pending";

    /// <summary>Unrecognized operation type — nothing was executed.</summary>
    public const string Unknown = "Unknown";

    /// <summary>The operation failed (chain error or exception).</summary>
    public const string Failed = "Failed";

    /// <summary>Terminal success for an op with no chain-specific success verb.</summary>
    public const string Completed = "Completed";

    /// <summary>Built server-side but handed to the client for signing — NOT
    /// broadcast server-side, so NOT yet irreversible.</summary>
    public const string AwaitingSignature = "AwaitingSignature";

    /// <summary>
    /// value-path-wiring M1: the transaction WAS broadcast server-side but did not
    /// confirm within the provider's poll window. The op carries a real
    /// <c>Parameters["TxHash"]</c> so reconciliation can settle it from chain truth.
    /// This is NOT a terminal state and is NOT a failure — it must never be promoted
    /// to a success or failure without positive chain signal, and a duplicate request
    /// must replay it as "in progress" rather than re-broadcast.
    /// </summary>
    public const string PendingConfirmation = "PendingConfirmation";

    /// <summary>
    /// value-path-wiring M1: message marker a provider prepends to a
    /// "submitted but not yet confirmed" SUCCESS result so the operation manager
    /// records <see cref="PendingConfirmation"/> + the TxHash instead of a terminal
    /// success/failure. Provider-neutral so any chain can signal the same state.
    /// </summary>
    public const string PendingConfirmationMarker = "Submitted, pending confirmation";

    // ─── Per-operation-type terminal success verbs ───

    public const string Minted = "Minted";
    public const string Burned = "Burned";
    public const string Exchanged = "Exchanged";
    public const string Swapped = "Swapped";
    public const string Transferred = "Transferred";
    public const string Deployed = "Deployed";
    public const string Called = "Called";
}
