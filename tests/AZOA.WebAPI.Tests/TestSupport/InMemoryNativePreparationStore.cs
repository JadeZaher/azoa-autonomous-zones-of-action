using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Tests.TestSupport;

internal sealed class InMemoryNativePreparationStore : INativePreparationStore
{
    private readonly Dictionary<string, NativePreparation> _rows = new();

    public Task<NativePreparation?> GetAsync(string id, CancellationToken ct = default)
    { lock (_rows) return Task.FromResult(_rows.GetValueOrDefault(id)); }

    public Task<NativePreparation> ClaimAsync(NativePreparation candidate, CancellationToken ct = default)
    { lock (_rows) { _rows.TryAdd(candidate.Id, candidate); return Task.FromResult(_rows[candidate.Id]); } }

    public Task<NativePreparation> SavePreparedAsync(string id, string intentHash, string preparedJson, CancellationToken ct = default)
    {
        lock (_rows)
        {
            var row = _rows[id];
            if (row.IntentHash != intentHash) throw new InvalidOperationException("Preparation intent changed.");
            row.PreparedJson ??= preparedJson;
            return Task.FromResult(row);
        }
    }
}
