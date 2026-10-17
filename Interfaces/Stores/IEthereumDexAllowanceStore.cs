using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Serializes allowance ownership through finalized execution or zero-allowance cleanup.</summary>
public interface IEthereumDexAllowanceStore
{
    /// <summary>Reads the exact allowance resource without granting signing authority.</summary>
    Task<EthereumDexAllowance?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Claims or replays ownership; a holder is replaced only after release (first retried from its saved confirmed stage) or provable never-exposed cancellation.</summary>
    Task<EthereumDexAllowance?> ClaimAsync(EthereumDexAllowance candidate, CancellationToken ct = default);

    /// <summary>Releases ownership only against a saved confirmed swap or allowance-revocation receipt.</summary>
    Task<bool> ReleaseAsync(string id, string operationId, string stageId, CancellationToken ct = default);
}
