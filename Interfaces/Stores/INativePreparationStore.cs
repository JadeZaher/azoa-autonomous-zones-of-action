using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Claims one immutable provider plan and fills its signed candidate once.</summary>
public interface INativePreparationStore
{
    Task<NativePreparation?> GetAsync(string id, CancellationToken ct = default);
    Task<NativePreparation> ClaimAsync(NativePreparation candidate, CancellationToken ct = default);
    Task<NativePreparation> SavePreparedAsync(string id, string intentHash, string preparedJson, CancellationToken ct = default);
}
