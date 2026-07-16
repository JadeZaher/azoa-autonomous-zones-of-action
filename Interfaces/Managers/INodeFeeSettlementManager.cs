using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
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
}
