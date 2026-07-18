using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface INodeFeeSettlementManager
{
    /// <summary>Validates and persists an inert, version-pinned settlement intent.</summary>
    Task<AZOAResult<NodeFeeSettlement>> PrepareAsync(
        NodeFeeSettlementDraft draft,
        CancellationToken ct = default);

    /// <summary>
    /// Claims a bounded set of due or stale-leased settlements and releases every won claim into
    /// <c>AwaitingReconciliation</c>. This safety-only worker seam never submits or confirms a chain effect.
    /// </summary>
    Task<AZOAResult<NodeFeeSettlementRecoveryReport>> RecoverDueAsync(
        NodeFeeSettlementRecoveryRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Persists accepted two-leg group evidence behind a live recovery lease and leaves the settlement
    /// awaiting independent chain reconciliation. This inert seam never submits, observes, or settles.
    /// </summary>
    Task<AZOAResult<NodeFeeAtomicGroup?>> RecordAcceptedAtomicGroupAsync(
        NodeFeeSettlementRecoveryLease lease,
        AtomicTransferGroupRequest request,
        AtomicTransferGroupSubmission submission,
        DateTimeOffset now,
        CancellationToken ct = default);
}
