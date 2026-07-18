using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>Observes accepted atomic-group receipts and advances only independently confirmed paired effects.</summary>
public interface INodeFeeSettlementAtomicGroupReconciler
{
    /// <summary>
    /// Claims a bounded set of due settlements, reads immutable accepted receipts, and observes exact
    /// chain evidence. This seam never signs, broadcasts, creates receipts, or activates a fee consumer.
    /// </summary>
    Task<AZOAResult<NodeFeeSettlementAtomicGroupReconciliationReport>> ReconcileDueAsync(
        NodeFeeSettlementRecoveryRequest request,
        CancellationToken ct = default);
}
