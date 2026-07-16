using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Governance;

/// <summary>Explicitly invoked inert recovery worker; it has no provider or transfer dependency.</summary>
public sealed class NodeFeeSettlementRecoveryWorker
{
    private readonly INodeFeeSettlementManager _settlements;

    public NodeFeeSettlementRecoveryWorker(INodeFeeSettlementManager settlements)
    {
        _settlements = settlements ?? throw new ArgumentNullException(nameof(settlements));
    }

    /// <summary>Runs one bounded recovery sweep without submitting or confirming any economic effect.</summary>
    public Task<AZOAResult<NodeFeeSettlementRecoveryReport>> RunOnceAsync(
        NodeFeeSettlementRecoveryRequest request,
        CancellationToken ct = default)
        => _settlements.RecoverDueAsync(request, ct);
}
