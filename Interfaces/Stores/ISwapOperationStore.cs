using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Atomically binds one scoped request to quote consumption or a cancellation tombstone.</summary>
public interface ISwapOperationStore
{
    /// <summary>Reads private original request state; transport callers enforce its actor/tenant scope.</summary>
    Task<SwapOperationRecord?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Replays the original request first, otherwise consumes its unexpired quote under current authority.</summary>
    Task<SwapOperationRecord?> AdmitAsync(SwapOperationRecord candidate, SigningAdmissionAuthority authority,
        CancellationToken ct = default);

    /// <summary>Creates a no-admission tombstone or cancels an admitted request only before any stage exists.</summary>
    Task<SwapOperationRecord?> CancelBeforePreparationAsync(SwapOperationRecord candidate,
        SigningConsentWitness authority, CancellationToken ct = default);

    /// <summary>Reads a private immutable stage; transport callers enforce the owning operation's scope.</summary>
    Task<SwapStageRecord?> GetStageAsync(string id, CancellationToken ct = default);

    /// <summary>Claims the first unsigned plan under current authority and the exact unprepared operation epoch.</summary>
    Task<SwapStageRecord?> ClaimInitialStageAsync(string operationId, long expectedVersion,
        SwapStagePlan plan, SigningAdmissionAuthority authority, CancellationToken ct = default);

    /// <summary>Checks live signing authority and advances both planned epochs immediately before custody, without permitting egress.</summary>
    Task<SwapStageRecord?> AdmitCustodyAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, SigningAdmissionAuthority authority, CancellationToken ct = default);

    /// <summary>Records potential external egress under current authority before any executable unsigned wire is disclosed.</summary>
    Task<SwapStageRecord?> ExposeStageAsync(string operationId, long expectedOperationVersion, string stageId,
        long expectedStageVersion, string exposureHash, IReadOnlyList<string> transactionIds,
        SigningAdmissionAuthority authority, CancellationToken ct = default);

    /// <summary>Fills the validated canonical signed candidate once without authorizing broadcast.</summary>
    Task<SwapStageRecord?> SaveSignedStageAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, PreparedSwapStage prepared, CancellationToken ct = default);

    /// <summary>Fills an exposed stage with its validated real signature without granting server submission authority.</summary>
    Task<SwapStageRecord?> SaveExposedCandidateAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, PreparedSwapStage prepared, CancellationToken ct = default);

    /// <summary>Atomically authorizes only the current saved candidate under current signing/action authority.</summary>
    Task<SwapStageRecord?> AuthorizeStageAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, SigningAdmissionAuthority authority, CancellationToken ct = default);

    /// <summary>Cancels a planned or signed stage only while first-send has never been authorized.</summary>
    Task<SwapStageRecord?> CancelUnsentStageAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, SigningConsentWitness authority, CancellationToken ct = default);

    /// <summary>Stops future server send admissions without asserting absence of exposed or already admitted transactions.</summary>
    Task<SwapStageRecord?> WithdrawSubmissionAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, SigningConsentWitness authority, CancellationToken ct = default);

    /// <summary>Records provider-validated evidence under paired epochs; terminal stage evidence is immutable, not operation completion.</summary>
    Task<SwapStageRecord?> RecordObservationAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, SwapStageObservation observation, CancellationToken ct = default);

    /// <summary>Operator-only: replaces the exact current operator-review observation with a provider-proven terminal settlement and its audit.</summary>
    Task<SwapStageRecord?> SettleOperatorReviewAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, SwapStageObservation settled, CancellationToken ct = default);

    /// <summary>Claims a successor under current authority and remaining caps, retaining full reservations for unresolved rent.</summary>
    Task<SwapStageRecord?> ClaimNextStageAsync(string operationId, long expectedVersion,
        SwapStagePlan plan, SigningAdmissionAuthority authority, CancellationToken ct = default);

    /// <summary>Reads at most 25 private predecessor stages; the last PreviousStageId is the continuation cursor.</summary>
    Task<IReadOnlyList<SwapStageRecord>> ReadHistoryPageAsync(string operationId, string? cursor = null,
        CancellationToken ct = default);

    /// <summary>Appends authoritative actual-rent proof once and atomically releases only that stage's unresolved reservation.</summary>
    Task<SwapStageRecord?> ResolveRentAccountingAsync(string operationId, long expectedOperationVersion,
        string stageId, long expectedStageVersion, string actualLockedBalance, string evidenceJson, CancellationToken ct = default);

    /// <summary>Commits one immutable economic receipt only against the current confirmed exact-input final stage.</summary>
    Task<SwapOperationRecord?> CompleteAsync(string operationId, long expectedOperationVersion, string stageId,
        long expectedStageVersion, IReadOnlyList<DexEffectDisclosure> retainedEffects, CancellationToken ct = default);

    /// <summary>Admits a new participant-authorized cleanup command only after original stages are terminal or provably unsent.</summary>
    Task<SwapOperationRecord?> BeginCleanupAsync(string operationId, long expectedOperationVersion,
        SwapCleanupConsent consent, SigningAdmissionAuthority authority, CancellationToken ct = default);

    /// <summary>Persists one provider history checkpoint and releases a proven allocation once under all three record epochs.</summary>
    Task<SwapStageRecord?> RecordRentReleaseAsync(string operationId, long expectedOperationVersion,
        string allocationStageId, long expectedAllocationVersion, string cleanupStageId, long expectedCleanupVersion,
        SwapRentReleaseProgress progress, CancellationToken ct = default);

    /// <summary>Records finalized cleanup only when every attributed rent reservation and release is reconciled.</summary>
    Task<SwapOperationRecord?> CompleteCleanupAsync(string operationId, long expectedOperationVersion,
        string cleanupStageId, long expectedStageVersion, CancellationToken ct = default);

    /// <summary>Reads a bounded due batch ordered by durable retry time, including unresolved accounting.</summary>
    Task<IReadOnlyList<SwapOperationRecord>> ReadRecoveryDueAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>Advances retry time before external observation so persistent failures do not monopolize a batch.</summary>
    Task<SwapOperationRecord?> ClaimRecoveryAsync(string operationId, long expectedVersion, CancellationToken ct = default);

    /// <summary>Persists the next private history page cursor without changing economic or signing authority.</summary>
    Task<bool> SaveRecoveryCursorAsync(string operationId, long expectedVersion, string? cursor, CancellationToken ct = default);
}
