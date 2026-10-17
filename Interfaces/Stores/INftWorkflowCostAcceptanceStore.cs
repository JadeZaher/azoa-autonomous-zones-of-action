using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Atomic explicit workflow cost acceptance and NFT admission.</summary>
public interface INftWorkflowCostAcceptanceStore
{
    /// <summary>Retires the exact uncommitted acceptance under current consent, preserving its original execution input.</summary>
    Task<bool> RetireAsync(NftWorkflowCostAcceptance request, AZOA.WebAPI.Models.Signing.SigningConsentWitness consent, CancellationToken ct = default);

    /// <summary>Reads the original immutable acceptance for the authenticated actor and exact client request.</summary>
    Task<NftWorkflowCostAcceptanceRecord?> GetAsync(Guid actor, Guid? tenant, Guid executionId, Guid requestId, CancellationToken ct = default);

    /// <summary>Creates both audit and lifecycle under original execution and current consent fences, or replays the original acceptance.</summary>
    Task<NftWorkflowCostAcceptanceRecord?> ClaimAsync(NftLifecycle candidate, NftWorkflowCostAcceptanceReceipt receipt, CancellationToken ct = default);
}
