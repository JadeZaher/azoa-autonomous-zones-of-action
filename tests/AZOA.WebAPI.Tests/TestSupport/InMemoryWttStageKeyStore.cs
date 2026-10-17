using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>Stage-key envelope double with the Surreal store's first-claim-wins and exact-envelope CAS rewrap rules.</summary>
internal sealed class InMemoryWttStageKeyStore : IWttStageKeyStore
{
    private readonly Dictionary<string, WttStageKeyEnvelope> _rows = new(StringComparer.Ordinal);

    public Task<WttStageKeyEnvelope?> GetAsync(string id, CancellationToken ct = default)
    { lock (_rows) return Task.FromResult(_rows.TryGetValue(id, out var row) ? Copy(row) : null); }

    public Task<WttStageKeyEnvelope> ClaimAsync(WttStageKeyEnvelope candidate, CancellationToken ct = default)
    {
        lock (_rows)
        {
            _rows.TryAdd(candidate.Id, Copy(candidate));
            return Task.FromResult(Copy(_rows[candidate.Id]));
        }
    }

    public Task<bool> TryRewrapAsync(WttStageKeyEnvelope expected, string newKeyVersion, string newEnvelopeJson, CancellationToken ct = default)
    {
        lock (_rows)
        {
            if (!_rows.TryGetValue(expected.Id, out var row) || row.Version != expected.Version || row.KeyVersion != expected.KeyVersion
                || row.EnvelopeJson != expected.EnvelopeJson || row.PublicKey != expected.PublicKey || row.BindingJson != expected.BindingJson)
                return Task.FromResult(false);
            row.KeyVersion = newKeyVersion; row.EnvelopeJson = newEnvelopeJson; row.Version++;
            return Task.FromResult(true);
        }
    }

    public Task<WttStageKeyEnvelope?> FindUnavailableKeyAsync(IReadOnlyCollection<string> availableVersions, CancellationToken ct = default)
    { lock (_rows) return Task.FromResult(_rows.Values.FirstOrDefault(row => !availableVersions.Contains(row.KeyVersion)) is { } row ? Copy(row) : null); }

    public Task<IReadOnlyList<WttStageKeyEnvelope>> FindNotOnVersionAsync(string activeVersion, int limit, CancellationToken ct = default)
    {
        lock (_rows)
            return Task.FromResult<IReadOnlyList<WttStageKeyEnvelope>>(_rows.Values.Where(row => row.KeyVersion != activeVersion)
                .OrderBy(row => row.Id, StringComparer.Ordinal).Take(limit).Select(Copy).ToArray());
    }

    private static WttStageKeyEnvelope Copy(WttStageKeyEnvelope row) => new()
    {
        Id = row.Id, BindingJson = row.BindingJson, PublicKey = row.PublicKey, KeyVersion = row.KeyVersion, EnvelopeJson = row.EnvelopeJson,
        Version = row.Version, CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt,
    };
}
