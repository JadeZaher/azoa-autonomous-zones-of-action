namespace AZOA.WebAPI.Services.Blockchain.ClientSigning;

/// <summary>Compare-and-swap persistence for client-signed operation rows (<c>operation_log</c>); see <c>Services/Blockchain/AGENTS.md</c>.</summary>
public interface IClientSignedOperationStore
{
    /// <summary>
    /// Moves the row from <paramref name="expectedStatus"/> to <paramref name="nextStatus"/> and replaces its parameter bag in one
    /// conditional write. False when the row already left <paramref name="expectedStatus"/> (another writer won).
    /// </summary>
    Task<bool> TryTransitionAsync(Guid operationId, string expectedStatus, string nextStatus,
        IReadOnlyDictionary<string, string> parameters, DateTimeOffset? completedAt, CancellationToken ct = default);

    /// <summary>Ids of rows still awaiting a client signature or confirmation since before <paramref name="awaitingBefore"/>, and of
    /// every <c>NeedsOperator</c> row, oldest first. The page may include custodial rows; callers keep those with a saved plan.</summary>
    Task<IReadOnlyList<Guid>> ListStuckAsync(DateTimeOffset awaitingBefore, int limit, CancellationToken ct = default);
}
