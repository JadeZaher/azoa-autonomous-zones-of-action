using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldCommandStore
{
    /// <inheritdoc/>
    public async Task<ExchangeHoldSource?> BeginExchangePreparationAsync(string sourceId, long expectedVersion, CancellationToken ct = default)
    {
        var source = await GetExchangeSourceAsync(sourceId, ct);
        var plan = ExchangePlan(source);
        if (source is null || plan is null || source.Version != expectedVersion || source.Version == long.MaxValue
            || source.State is not ("Committed" or "Preparing") || source.PreparedWireJson is not null) return null;
        var intentJson = JsonSerializer.Serialize(plan.Value.Intent);
        if (source.WireIntentJson is not null && source.WireIntentJson != intentJson) return null;
        var update = SurrealWriter.UpdateOnly<ExchangeHoldSource>(source.Id)
            .Where(row => row.Version == expectedVersion && row.State == source.State && row.IntentJson == source.IntentJson
                && row.AuthorityJson == source.AuthorityJson && row.WireIntentJson == source.WireIntentJson && row.PreparedWireJson == null)
            .Set(row => row.State, "Preparing").Set(row => row.WireIntentJson, intentJson)
            .Set(row => row.Version, expectedVersion + 1).Set(row => row.UpdatedAt, DateTimeOffset.UtcNow).Build();
        return await ExchangeWireMutationAsync(update, plan.Value.Authority, ct);
    }

    /// <inheritdoc/>
    public async Task<ExchangeHoldSource?> SaveExchangePreparedAsync(string sourceId, long expectedVersion,
        PreparedExchangeWire prepared, CancellationToken ct = default)
    {
        var source = await GetExchangeSourceAsync(sourceId, ct);
        var plan = ExchangePlan(source);
        if (source is null || plan is null || source.Version != expectedVersion || source.Version == long.MaxValue
            || source.State != "Preparing" || source.PreparedWireJson is not null
            || source.WireIntentJson != JsonSerializer.Serialize(plan.Value.Intent)
            || CanonicalJson.Hash(prepared.Intent) != CanonicalJson.Hash(plan.Value.Intent)
            || string.IsNullOrWhiteSpace(prepared.PreparationId) || string.IsNullOrWhiteSpace(prepared.GroupId)
            || prepared.TransactionIds.Count == 0 || prepared.TransactionIds.Count != prepared.SignedTransactionsBase64.Count
            || prepared.TransactionIds.Any(string.IsNullOrWhiteSpace) || prepared.SignedTransactionsBase64.Any(string.IsNullOrWhiteSpace)) return null;
        var update = SurrealWriter.UpdateOnly<ExchangeHoldSource>(source.Id)
            .Where(row => row.Version == expectedVersion && row.State == "Preparing" && row.IntentJson == source.IntentJson
                && row.AuthorityJson == source.AuthorityJson && row.WireIntentJson == source.WireIntentJson && row.PreparedWireJson == null)
            .Set(row => row.PreparedWireJson, JsonSerializer.Serialize(prepared)).Set(row => row.State, "Prepared")
            .Set(row => row.PreparedHash, CanonicalJson.Hash(prepared))
            .Set(row => row.Version, expectedVersion + 1).Set(row => row.UpdatedAt, DateTimeOffset.UtcNow).Build();
        return await ExchangeWireMutationAsync(update, null, ct);
    }

    /// <inheritdoc/>
    public async Task<ExchangeHoldSource?> AuthorizeExchangeSubmissionAsync(string sourceId, long expectedVersion, CancellationToken ct = default)
    {
        var source = await GetExchangeSourceAsync(sourceId, ct);
        var plan = ExchangePlan(source);
        var prepared = source?.PreparedWireJson is { } wire ? JsonSerializer.Deserialize<PreparedExchangeWire>(wire) : null;
        if (source is null || plan is null || prepared is null || source.Version != expectedVersion || source.Version == long.MaxValue
            || source.State != "Prepared" || source.WireIntentJson != JsonSerializer.Serialize(plan.Value.Intent)
            || source.PreparedHash != CanonicalJson.Hash(prepared)
            || CanonicalJson.Hash(prepared.Intent) != CanonicalJson.Hash(plan.Value.Intent)) return null;
        var update = SurrealWriter.UpdateOnly<ExchangeHoldSource>(source.Id)
            .Where(row => row.Version == expectedVersion && row.State == "Prepared" && row.IntentJson == source.IntentJson
                && row.AuthorityJson == source.AuthorityJson && row.WireIntentJson == source.WireIntentJson
                && row.PreparedWireJson == source.PreparedWireJson && row.PreparedHash == source.PreparedHash)
            .Set(row => row.State, "Submitted").Set(row => row.Version, expectedVersion + 1)
            .Set(row => row.UpdatedAt, DateTimeOffset.UtcNow).Build();
        return await ExchangeWireMutationAsync(update, plan.Value.Authority, ct);
    }

    /// <inheritdoc/>
    public async Task<ExchangeHoldSource?> RecordExchangeEvidenceAsync(string sourceId, long expectedVersion,
        ExchangeWireObservation evidence, CancellationToken ct = default)
    {
        var source = await GetExchangeSourceAsync(sourceId, ct);
        var prepared = source?.PreparedWireJson is { } wire ? JsonSerializer.Deserialize<PreparedExchangeWire>(wire) : null;
        if (source is null || prepared is null || source.Version != expectedVersion || source.Version == long.MaxValue
            || source.State != "Submitted" || evidence.Verdict is not ("Confirmed" or "ExpiredAbsent")
            || evidence.PreparedHash != CanonicalJson.Hash(prepared) || evidence.PreparationId != prepared.PreparationId
            || evidence.PreparedHash != source.PreparedHash
            || evidence.GroupId != prepared.GroupId || evidence.ProtocolIdentity != prepared.Intent.ProtocolIdentity
            || evidence.Verdict == "Confirmed" && (!UnsignedIntegerText.TryParse(evidence.FinalizedPosition, out var final) || final <= 0)) return null;
        var update = SurrealWriter.UpdateOnly<ExchangeHoldSource>(source.Id)
            .Where(row => row.Version == expectedVersion && row.State == "Submitted" && row.PreparedWireJson == source.PreparedWireJson
                && row.SettlementEvidenceJson == null)
            .Set(row => row.State, evidence.Verdict).Set(row => row.SettlementEvidenceJson, JsonSerializer.Serialize(evidence))
            .Set(row => row.Version, expectedVersion + 1).Set(row => row.UpdatedAt, DateTimeOffset.UtcNow).Build();
        return await ExchangeWireMutationAsync(update, null, ct);
    }

    private static (ExchangeWireIntent Intent, ExchangeParticipantAuthority Authority)? ExchangePlan(ExchangeHoldSource? source)
    {
        if (source is null || source.IntentHash != CanonicalJson.HashCanonical(source.IntentJson)) return null;
        var intent = JsonSerializer.Deserialize<ExchangeHoldSourceIntent>(source.IntentJson);
        var authority = JsonSerializer.Deserialize<ExchangeParticipantAuthority>(source.AuthorityJson);
        if (intent is null || authority is null || !ValidExchangeSource(intent, authority)
            || source.Id != ExchangeHoldSourceIdentity.ForIntent(intent)
            || source.HoldCommandId != (intent.Opening is null ? null : HoldCommandIdentity.Operation(intent.Opening))
            || source.HoldRecordId != (intent.Opening is null ? null : HoldCommandIdentity.Hold(intent.Opening))) return null;
        var wire = ExchangeHoldWirePlan.Create(source.Id, source.Generation, intent, authority);
        return wire is null ? null : (wire, authority);
    }

    private async Task<ExchangeHoldSource?> ExchangeWireMutationAsync(SurrealQuery mutation,
        ExchangeParticipantAuthority? authority, CancellationToken ct)
    {
        var guards = authority is null ? Array.Empty<SurrealQuery>() : ExchangeAuthorityFence.Create(authority, "exchange_wire");
        if (guards is null) return null;
        var query = mutation;
        var resultIndex = 0;
        if (guards.Count > 0)
        {
            // raw: first custody/egress authorization and the exact source version commit under both original authorities.
            var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
            statements.AddRange(guards);
            statements.Add(SurrealGeneratedStatementScope.Assign(mutation, "exchange_wire_update", "_exchange_wire_row"));
            resultIndex = statements.Count;
            statements.Add(SurrealQuery.Of("RETURN $_exchange_wire_row"));
            statements.Add(SurrealQuery.Of("COMMIT"));
            query = SurrealQuery.Combine(statements.ToArray());
        }
        try
        {
            return await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct);
                response.EnsureAllOk();
                return response.GetValues<ExchangeHoldSource>(resultIndex).SingleOrDefault();
            }, ct);
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(ExchangeAuthorityFence.FailureMarker, StringComparison.Ordinal)) { return null; }
    }
}
