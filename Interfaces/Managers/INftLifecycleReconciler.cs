using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>Observes admitted NFT egress and projects evidence without creating signatures or stages.</summary>
public interface INftLifecycleReconciler
{
    /// <summary>Reconciles only already submitted wire or confirmed projection; other states remain unchanged.</summary>
    Task<AZOAResult<NftLifecycleView>> ReconcileAsync(string operationId, CancellationToken ct = default);
}
