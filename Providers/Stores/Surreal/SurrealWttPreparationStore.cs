using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealWttPreparationStore(ISurrealExecutor executor) : IWttPreparationStore
{
    /// <inheritdoc/>
    public Task<WttPreparation?> GetAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<WttPreparation>(SurrealQuery<WttPreparation>.Key(id), ct);

    /// <inheritdoc/>
    public async Task<WttPreparation> ClaimAsync(WttPreparation candidate, CancellationToken ct = default)
    {
        if (candidate.Id != WttPreparationIdentity.Id(WttPreparationIdentity.Parse(candidate.BindingJson))
            || candidate.Version != 0 || candidate.Sealed || candidate.SignedPreparationJson is not null
            || string.IsNullOrWhiteSpace(candidate.PlanJson))
            throw new ArgumentException("Invalid WTT unsigned preparation candidate.", nameof(candidate));
        try
        {
            var response = await executor.ExecuteAsync(SurrealWriter.Create(candidate), ct);
            response.EnsureAllOk();
            return response.GetValues<WttPreparation>(0).Single();
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var winner = await GetAsync(candidate.Id, ct) ?? throw new InvalidOperationException("WTT preparation claim disappeared.");
            if (winner.BindingJson != candidate.BindingJson) throw new InvalidOperationException("WTT stage belongs to another immutable quote or intent.");
            return winner;
        }
    }

    /// <inheritdoc/>
    public async Task<WttPreparation?> TrySaveSignedAsync(WttPreparation expected, string signedPreparationJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(signedPreparationJson)) throw new ArgumentException("Signed preparation is required.", nameof(signedPreparationJson));
        var response = await executor.ExecuteAsync(SurrealWriter.UpdateOnly<WttPreparation>(expected.Id)
            .Where(row => row.Version == expected.Version && !row.Sealed && row.SignedPreparationJson == null
                && row.BindingJson == expected.BindingJson && row.PlanJson == expected.PlanJson)
            .Set(row => row.SignedPreparationJson, signedPreparationJson).Set(row => row.Version, checked(expected.Version + 1))
            .Set(row => row.UpdatedAt, DateTimeOffset.UtcNow).Build(), ct);
        response.EnsureAllOk();
        return response.GetValues<WttPreparation>(0).SingleOrDefault();
    }

    /// <inheritdoc/>
    public async Task<WttPreparation?> SealAsync(string id, string bindingJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id) || id != WttPreparationIdentity.Id(WttPreparationIdentity.Parse(bindingJson)))
            throw new ArgumentException("Sealing requires the exact immutable preparation identity.", nameof(bindingJson));
        var current = await GetAsync(id, ct);
        if (current is null || current.BindingJson != bindingJson) return null;
        if (current.Sealed) return current;
        var response = await executor.ExecuteAsync(SurrealWriter.UpdateOnly<WttPreparation>(id)
            .Where(row => row.Version == current.Version && row.BindingJson == bindingJson && !row.Sealed)
            .Set(row => row.Sealed, true).Set(row => row.Version, checked(current.Version + 1))
            .Set(row => row.UpdatedAt, DateTimeOffset.UtcNow).Build(), ct);
        response.EnsureAllOk();
        return response.GetValues<WttPreparation>(0).SingleOrDefault() ?? await GetAsync(id, ct);
    }
}
