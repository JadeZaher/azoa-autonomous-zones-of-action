using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Bridge;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence boundary for cross-chain bridge transactions and the
/// replay-protection ledger. This is the exactly-once primitive: the
/// conditional transition methods return the affected-row count VERBATIM and
/// the store NEVER asserts==1, retries, or read-modify-writes. All status
/// policy (what a 0 vs 1 row count means) stays in the caller.
/// </summary>
public interface IBridgeStore
{
    /// <summary>Loads a single bridge transaction by id, or null if absent.</summary>
    Task<BridgeTransactionResult?> GetBridgeAsync(string id, CancellationToken ct = default);

    /// <summary>Loads an avatar's bridge transaction history.</summary>
    Task<IReadOnlyList<BridgeTransactionResult>> GetBridgeHistoryAsync(Guid avatarId, bool descending = false, CancellationToken ct = default);

    /// <summary>Loads ids of non-terminal bridges last touched before <paramref name="staleBefore"/>, capped at <paramref name="batch"/>.</summary>
    Task<IReadOnlyList<string>> GetNonTerminalBridgeIdsAsync(IReadOnlyCollection<BridgeStatus> nonTerminal, DateTime staleBefore, int batch, CancellationToken ct = default);

    /// <summary>Loads ids of non-terminal operations last touched before <paramref name="staleBefore"/>, capped at <paramref name="batch"/>.</summary>
    Task<IReadOnlyList<Guid>> GetNonTerminalOperationIdsAsync(IReadOnlyCollection<string> nonTerminal, DateTime staleBefore, int batch, CancellationToken ct = default);

    /// <summary>Loads a single blockchain operation by id, or null if absent.</summary>
    Task<BlockchainOperation?> GetOperationAsync(Guid id, CancellationToken ct = default);

    /// <summary>Inserts a new bridge transaction.</summary>
    Task AddBridgeAsync(BridgeTransactionResult tx, CancellationToken ct = default);

    /// <summary>
    /// Attempts to record a consumed VAA. Returns false iff the
    /// UNIQUE(Digest) constraint rejected the insert — that is a detected
    /// replay; true means this VAA was recorded for the first time.
    /// </summary>
    Task<bool> TryInsertConsumedVaaAsync(ConsumedVaaRecord record, CancellationToken ct = default);

    /// <summary>Persists a fetched VAA's bytes/signature-count/proof and advances status to <paramref name="statusVAAReady"/>.</summary>
    Task SaveVaaFetchResultAsync(string id, string vaaBytes, int sigCount, string proofData, BridgeStatus statusVAAReady, CancellationToken ct = default);

    /// <summary>
    /// Conditional status transition: UPDATE … WHERE Id=id AND Status=expected
    /// SET Status=next plus any non-null <paramref name="alsoSet"/> fields.
    /// Returns the affected-row count VERBATIM (0 = lost the race / wrong
    /// state; 1 = won). The store never asserts==1, retries, or RMW.
    /// </summary>
    Task<int> TryTransitionBridgeStatusAsync(string id, BridgeStatus expected, BridgeStatus next, BridgeStatusMutation? alsoSet, CancellationToken ct = default);

    /// <summary>
    /// Conditional operation status transition: UPDATE … WHERE Id=id AND
    /// Status=expected SET Status=next (and CompletedDate when supplied).
    /// Returns the affected-row count VERBATIM. The store never
    /// asserts==1, retries, or RMW.
    /// </summary>
    Task<int> TryTransitionOperationStatusAsync(Guid id, string expected, string next, DateTime? completedDate, CancellationToken ct = default);

    /// <summary>Returns true iff a bridge with the given id exists.</summary>
    Task<bool> ExistsByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Records a VAA-fetch error message on the bridge row WITHOUT advancing
    /// status. Mirrors the legacy "tx.ErrorMessage = ...; SaveChangesAsync()"
    /// pattern from CrossChainBridgeService.FetchVAAAsync. The store does NOT
    /// gate on status — caller already validated the row before fetching.
    /// </summary>
    Task RecordVaaFetchErrorAsync(string id, string errorMessage, CancellationToken ct = default);

    /// <summary>
    /// Force-completes a bridge from any non-Completed status: UPDATE … WHERE
    /// Id=id AND Status != Completed SET Status=Completed, CompletedAt=UtcNow.
    /// Returns affected-row count VERBATIM (0 = already Completed / not found;
    /// 1 = transitioned). The store NEVER asserts==1, retries, or RMW.
    /// Mirrors the legacy CompleteBridgeAsync force-complete pattern.
    /// </summary>
    Task<int> ForceCompleteBridgeAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Looks up the bridge row stamped with the given idempotency key, or null
    /// if no row has been stamped. Used by the idempotent-replay path of
    /// InitiateTrustedBridgeAsync to return the prior committed row when the
    /// idempotency ledger reports Completed.
    /// </summary>
    Task<BridgeTransactionResult?> GetBridgeByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);
}
