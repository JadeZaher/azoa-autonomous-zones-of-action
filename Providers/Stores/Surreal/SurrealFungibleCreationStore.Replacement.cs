using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealFungibleCreationStore
{
    /// <inheritdoc/>
    public async Task<FungibleCreationOperation?> ReplaceAsync(FungibleCreationOperation expected, FungibleCreationAuthorization authorization,
        CancellationToken ct = default)
    {
        var previous = await executor.QuerySingleAsync<FungibleCreationAuthorization>(SurrealQuery<FungibleCreationAuthorization>.Key(authorization.Id), ct);
        if (previous is not null)
            return previous.OperationId == expected.Id && previous.ActorId == expected.ActorUserId && previous.TenantId == expected.TenantId
                && previous.RequestHash == authorization.RequestHash ? await GetAsync(expected.Id, ct) : null;
        var command = Command(expected);
        if (expected.State != "FailedNoEffect" || !HasNoEffectProof(expected) || expected.Generation == int.MaxValue
            || authorization.ExpectedVersion != expected.Version || authorization.OperationId != expected.Id
            || authorization.ActorId != expected.ActorUserId || authorization.TenantId != expected.TenantId
            || authorization.PreparedHash is not null) return null;
        var nextIntent = JsonSerializer.Deserialize<FungibleCreationIntent>(expected.IntentJson)! with { Generation = expected.Generation + 1, Quote = null };
        if (authorization.IntentHash != FungibleCreationIdentity.IntentHash(nextIntent)) return null;
        var fence = FungibleCreationAuthorityFence.Create(command, authorization.AuthorityJson, "fungible_replacement", freshParticipant: true);
        if (fence is null) return null;
        var history = JsonSerializer.Deserialize<List<FungibleCreationGeneration>>(expected.HistoryJson)!;
        history.Add(new(expected.Generation, expected.IntentJson, expected.PreparedJson!, expected.ObservationJson!,
            expected.CurrentAuthorityId, expected.CurrentAuthorityJson));
        // raw: exact final absence, new generation consent and original workflow serialize with an immutable audit.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(fence);
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<FungibleCreationOperation>(expected.Id)
            .Where(row => row.Version == expected.Version && row.State == "FailedNoEffect" && row.RequestHash == expected.RequestHash
                && row.IntentJson == expected.IntentJson && row.PreparedJson == expected.PreparedJson && row.ObservationJson == expected.ObservationJson
                && row.Generation == expected.Generation && row.HistoryJson == expected.HistoryJson)
            .Set(row => row.State, "AwaitingConsent").Set(row => row.Version, expected.Version + 1).Set(row => row.Generation, nextIntent.Generation)
            .Set(row => row.IntentJson, JsonSerializer.Serialize(nextIntent)).Set(row => row.HistoryJson, JsonSerializer.Serialize(history))
            .Set(row => row.CurrentAuthorityId, authorization.Id).Set(row => row.CurrentAuthorityJson, authorization.AuthorityJson)
            .Set(row => row.AcceptedAt, DateTimeOffset.UnixEpoch).Set(row => row.QuoteExpiresAt, DateTimeOffset.UnixEpoch)
            .Unset(row => row.PreparedJson).Unset(row => row.ObservationJson).Unset(row => row.OfferedQuoteJson).Unset(row => row.AssetId)
            .Set(row => row.Detail, "The prior generation proved no chain effect. This generation requires a new displayed cost quote and consent.")
            .Set(row => row.UpdatedAt, DateTimeOffset.UtcNow).Build(), "fungible_replace_operation", "_fungible_replace_result"));
        statements.Add(SurrealQuery.Of("IF $_fungible_replace_result = NONE { THROW 'FUNGIBLE_CREATION_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(authorization), "fungible_replace_authority"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var transaction = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(transaction, ct);
                response.EnsureAllOk();
                return response;
            }, ct);
            return await GetAsync(expected.Id, ct);
        }
        catch (SurrealStatementException exception) when (IsConflict(exception))
        {
            previous = await executor.QuerySingleAsync<FungibleCreationAuthorization>(SurrealQuery<FungibleCreationAuthorization>.Key(authorization.Id), ct);
            return previous?.RequestHash == authorization.RequestHash ? await GetAsync(expected.Id, ct) : null;
        }
    }
}
