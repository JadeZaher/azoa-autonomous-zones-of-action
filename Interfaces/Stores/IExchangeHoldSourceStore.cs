using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Journals an exchange purchase before committing its exact reserve liability.</summary>
public interface IExchangeHoldSourceStore
{
    /// <summary>Reads immutable source identity without authorizing signing or settlement.</summary>
    Task<ExchangeHoldSource?> GetExchangeSourceAsync(string sourceId, CancellationToken ct = default);

    /// <summary>Admits the exact source under both the actual tenant and participant's current authority.</summary>
    Task<ExchangeHoldSource?> AdmitExchangeSourceAsync(ExchangeHoldSourceIntent intent,
        ExchangeParticipantAuthority authority, CancellationToken ct = default);

    /// <summary>Commits source linkage, original hold receipt and reserve accounting in one transaction.</summary>
    Task<HoldCommandReceipt?> CommitExchangeOpeningAsync(string sourceId, CancellationToken ct = default);

    /// <summary>Commits the source's exact reserve, hold or treasury commitment under both original authorities; returns the current source.</summary>
    Task<ExchangeHoldSource?> CommitExchangeAsync(string sourceId, CancellationToken ct = default);

    /// <summary>Replaces only an unaccepted, uncommitted release disclosure with a fresh quote for unchanged terms, keeping an immutable audit.</summary>
    Task<ExchangeHoldSource?> ReviseExchangeReleaseAsync(string sourceId, ExchangeReleaseRevision revision, CancellationToken ct = default);

    /// <summary>Claims an exact provider intent under the original two-party authority before custody.</summary>
    Task<ExchangeHoldSource?> BeginExchangePreparationAsync(string sourceId, long expectedVersion, CancellationToken ct = default);

    /// <summary>Persists only the prepared candidate matching the previously authorized intent.</summary>
    Task<ExchangeHoldSource?> SaveExchangePreparedAsync(string sourceId, long expectedVersion,
        PreparedExchangeWire prepared, CancellationToken ct = default);

    /// <summary>Authorizes first egress of the exact saved wire under both current original authorities.</summary>
    Task<ExchangeHoldSource?> AuthorizeExchangeSubmissionAsync(string sourceId, long expectedVersion, CancellationToken ct = default);

    /// <summary>Records exact authoritative settlement or no-effect proof without renewing signing authority.</summary>
    Task<ExchangeHoldSource?> RecordExchangeEvidenceAsync(string sourceId, long expectedVersion,
        ExchangeWireObservation evidence, CancellationToken ct = default);

    /// <summary>Atomically activates the exact funded liability and credits its original treasury once.</summary>
    Task<ExchangeHoldSettlementReceipt?> CompleteExchangeSettlementAsync(string sourceId, CancellationToken ct = default);

    /// <summary>Returns only the original reserve commitment after exact authoritative no-effect proof.</summary>
    Task<ExchangeHoldSettlementReceipt?> CompensateExchangeAbsenceAsync(string sourceId, CancellationToken ct = default);

    /// <summary>Releases the commitment of a source never authorized for transmission once it has been idle since <paramref name="staleBefore"/>.</summary>
    Task<ExchangeHoldSettlementReceipt?> AbandonUnsentExchangeAsync(string sourceId, DateTimeOffset staleBefore, CancellationToken ct = default);

    /// <summary>Lists a bounded due page of sources awaiting observation, accounting or abandonment; grants no new authority.</summary>
    Task<IReadOnlyList<ExchangeHoldSource>> ListExchangeReconciliationAsync(DateTimeOffset now, int limit, CancellationToken ct = default);

    /// <summary>Advances the observed version's schedule without changing its state or authority.</summary>
    Task<bool> ScheduleExchangeReconciliationAsync(ExchangeHoldSource expected, DateTimeOffset next, CancellationToken ct = default);

    /// <summary>Lists one participant's settled purchases oldest-first; the immutable input to reversal attribution.</summary>
    Task<IReadOnlyList<ExchangeHoldSource>> ListParticipantPurchasesAsync(Guid participant, int limit, CancellationToken ct = default);

    /// <summary>Recovers only what this reversal has not yet taken from the purchase, within the lot's attributed share; null when nothing more is due.</summary>
    Task<ExchangePurchaseRecoveryResult?> RecoverExchangePurchaseAsync(ExchangePurchaseRecovery request, CancellationToken ct = default);

    /// <summary>
    /// Reserves a clawback of delivered units for the same cumulative want, before anything is sent: the step's receipt is
    /// the clawback's idempotency anchor. Null when nothing more is due or deliverable.
    /// </summary>
    Task<ExchangePurchaseRecoveryResult?> ReserveExchangeClawbackAsync(ExchangePurchaseRecovery request, CancellationToken ct = default);

    /// <summary>Every purchase recovery and clawback step one reversal has committed, oldest first.</summary>
    Task<IReadOnlyList<ExchangePurchaseRecoveryResult>> ListReversalRecoveriesAsync(string reversalId, Guid subjectUserId,
        CancellationToken ct = default);

    /// <summary>The settled held purchase's current liability, following recovery remainders; null when it has none.</summary>
    Task<ExchangePurchaseLiability?> GetPurchaseLiabilityAsync(string sourceId, CancellationToken ct = default);

    /// <summary>Lists the actual tenant API key's own sources, newest first, for discovery after a lost response.</summary>
    Task<IReadOnlyList<ExchangeHoldSource>> ListExchangeSourcesAsync(Guid tenant, Guid apiKey, int offset, int limit, CancellationToken ct = default);
}
