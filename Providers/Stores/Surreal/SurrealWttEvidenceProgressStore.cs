using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealWttEvidenceProgressStore(ISurrealExecutor executor) : IWttEvidenceProgressStore
{
    /// <inheritdoc/>
    public async Task<WttEvidenceProgress> ClaimAsync(string bindingJson, string initialStateJson, CancellationToken ct = default)
    {
        var candidate = WttEvidenceProgressContract.Begin(bindingJson, initialStateJson);
        try
        {
            var response = await executor.ExecuteAsync(SurrealWriter.Create(candidate), ct);
            response.EnsureAllOk(); return response.GetValues<WttEvidenceProgress>(0).Single();
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var existing = await executor.QuerySingleAsync<WttEvidenceProgress>(SurrealQuery<WttEvidenceProgress>.Key(candidate.Id), ct);
            return existing is not null && existing.BindingJson == candidate.BindingJson && existing.InitialStateJson == initialStateJson
                ? existing : throw new InvalidOperationException("WTT evidence cursor conflicts with its original immutable query.");
        }
    }

    /// <inheritdoc/>
    public async Task<WttEvidenceProgress?> AdvanceAsync(WttEvidenceProgress expected, string stateJson, string pageDigest,
        string? nextCursor, CancellationToken ct = default)
    {
        var next = WttEvidenceProgressContract.Advance(expected, stateJson, pageDigest, nextCursor);
        var update = SurrealWriter.UpdateOnly<WttEvidenceProgress>(expected.Id)
            .Where(row => row.Version == expected.Version && !row.Complete && row.BindingJson == expected.BindingJson
                && row.InitialStateJson == expected.InitialStateJson && row.StateJson == expected.StateJson
                && row.Digest == expected.Digest && row.Pages == expected.Pages && row.NextCursor == expected.NextCursor
                && row.SeenCursorHashesJson == expected.SeenCursorHashesJson)
            .Set(row => row.StateJson, next.StateJson).Set(row => row.SeenCursorHashesJson, next.SeenCursorHashesJson)
            .Set(row => row.Digest, next.Digest).Set(row => row.Pages, next.Pages).Set(row => row.Version, next.Version)
            .Set(row => row.Complete, next.Complete).Set(row => row.UpdatedAt, next.UpdatedAt);
        update = next.NextCursor is null ? update.Unset(row => row.NextCursor) : update.Set(row => row.NextCursor, next.NextCursor);
        var response = await executor.ExecuteAsync(update.Build(), ct);
        response.EnsureAllOk(); return response.GetValues<WttEvidenceProgress>(0).SingleOrDefault();
    }
}
