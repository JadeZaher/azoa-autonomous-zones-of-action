namespace OASIS.WebAPI.Models;

/// <summary>
/// The closed set of <see cref="BlockchainOperation.Status"/> values. Every
/// producer (<see cref="OASIS.WebAPI.Managers.BlockchainOperationManager"/>)
/// and consumer (<see cref="OASIS.WebAPI.Services.Reconciliation.ReconciliationService"/>)
/// references these constants instead of bare string literals, so a typo or a
/// new state is a compile-time concern, not a silent runtime divergence.
///
/// <para>Kept as <c>const string</c> (not an <c>enum</c>) on purpose:
/// <see cref="OASIS.WebAPI.Interfaces.IBlockchainOperation.Status"/> is a
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

    // ─── Per-operation-type terminal success verbs ───

    public const string Minted = "Minted";
    public const string Burned = "Burned";
    public const string Exchanged = "Exchanged";
    public const string Swapped = "Swapped";
    public const string Transferred = "Transferred";
    public const string Deployed = "Deployed";
    public const string Called = "Called";
}
