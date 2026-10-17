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
}
