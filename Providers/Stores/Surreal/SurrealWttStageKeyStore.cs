using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealWttStageKeyStore(ISurrealExecutor executor) : IWttStageKeyStore
{
    /// <inheritdoc/>
    public Task<WttStageKeyEnvelope?> GetAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<WttStageKeyEnvelope>(SurrealQuery<WttStageKeyEnvelope>.Key(id), ct);

    /// <inheritdoc/>
    public async Task<WttStageKeyEnvelope> ClaimAsync(WttStageKeyEnvelope candidate, CancellationToken ct = default)
    {
        if (candidate.Id != CanonicalJson.HashCanonical(candidate.BindingJson) || candidate.Version != 0
            || string.IsNullOrWhiteSpace(candidate.PublicKey) || string.IsNullOrWhiteSpace(candidate.KeyVersion))
            throw new ArgumentException("Invalid immutable WTT stage key candidate.", nameof(candidate));
        try
        {
            var response = await executor.ExecuteAsync(SurrealWriter.Create(candidate), ct);
            response.EnsureAllOk();
            return response.GetValues<WttStageKeyEnvelope>(0).Single();
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var winner = await GetAsync(candidate.Id, ct)
                ?? throw new InvalidOperationException("WTT stage key claim disappeared.");
            if (winner.BindingJson != candidate.BindingJson)
                throw new InvalidOperationException("WTT stage key identity collision.");
            return winner;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> TryRewrapAsync(WttStageKeyEnvelope expected, string newKeyVersion, string newEnvelopeJson, CancellationToken ct = default)
    {
        var response = await executor.ExecuteAsync(SurrealWriter.UpdateOnly<WttStageKeyEnvelope>(expected.Id)
            .Where(row => row.Version == expected.Version && row.BindingJson == expected.BindingJson
                && row.PublicKey == expected.PublicKey && row.KeyVersion == expected.KeyVersion && row.EnvelopeJson == expected.EnvelopeJson)
            .Set(row => row.KeyVersion, newKeyVersion).Set(row => row.EnvelopeJson, newEnvelopeJson)
            .Set(row => row.Version, checked(expected.Version + 1)).Set(row => row.UpdatedAt, DateTimeOffset.UtcNow).Build(), ct);
        response.EnsureAllOk();
        return response.GetValues<WttStageKeyEnvelope>(0).SingleOrDefault() is not null;
    }

    /// <inheritdoc/>
    public Task<WttStageKeyEnvelope?> FindUnavailableKeyAsync(IReadOnlyCollection<string> availableVersions, CancellationToken ct = default)
    {
        var versions = availableVersions.ToArray();
        return executor.QuerySingleAsync<WttStageKeyEnvelope>(SurrealQuery<WttStageKeyEnvelope>.From()
            .Where(row => !versions.Contains(row.KeyVersion)).Limit(1), ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WttStageKeyEnvelope>> FindNotOnVersionAsync(string activeVersion, int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > 500 || string.IsNullOrWhiteSpace(activeVersion)) return [];
        return await executor.QueryAsync<WttStageKeyEnvelope>(SurrealQuery<WttStageKeyEnvelope>.From()
            .Where(row => row.KeyVersion != activeVersion).OrderBy(row => row.Id).Limit(limit), ct);
    }
}
