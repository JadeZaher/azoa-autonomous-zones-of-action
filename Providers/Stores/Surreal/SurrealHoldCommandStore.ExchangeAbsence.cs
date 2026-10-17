using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Reserve;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldCommandStore
{
    /// <inheritdoc/>
    public async Task<ExchangeHoldSettlementReceipt?> CompensateExchangeAbsenceAsync(string sourceId, CancellationToken ct = default)
    {
        var source = await GetExchangeSourceAsync(sourceId, ct);
        var plan = ExchangePlan(source);
        if (source is null || plan is null || source.PreparedWireJson is null || source.SettlementEvidenceJson is null) return null;
        var intent = JsonSerializer.Deserialize<ExchangeHoldSourceIntent>(source.IntentJson)!;
        var prepared = JsonSerializer.Deserialize<PreparedExchangeWire>(source.PreparedWireJson)!;
        var evidence = JsonSerializer.Deserialize<ExchangeWireObservation>(source.SettlementEvidenceJson)!;
        if (evidence.Verdict != "ExpiredAbsent" || evidence.PreparedHash != CanonicalJson.Hash(prepared)
            || source.PreparedHash != evidence.PreparedHash
            || evidence.PreparationId != prepared.PreparationId || evidence.GroupId != prepared.GroupId
            || evidence.ProtocolIdentity != plan.Value.Intent.ProtocolIdentity
            || CanonicalJson.Hash(prepared.Intent) != CanonicalJson.Hash(plan.Value.Intent)) return null;
        var prior = await executor.QuerySingleAsync<ExchangeHoldSettlementReceipt>(SurrealQuery<ExchangeHoldSettlementReceipt>.Key(sourceId), ct);
        if (prior is not null) return SameExchangeSettlement(prior, source, evidence) ? prior : null;
        if (source.State != "ExpiredAbsent" || source.Version == long.MaxValue) return null;
        var hold = await GetHoldAsync(source.HoldRecordId!, ct);
        if (hold is null || hold.EconomicSourceId != sourceId || hold.State != "AwaitingSourceSettlement"
            || hold.OpeningCommandId != source.HoldCommandId || hold.DefinitionHash != CanonicalJson.Hash(intent.Opening.Definition)
            || hold.Amount != intent.Opening.Definition!.QuantityBaseUnits || hold.ReservePartition != intent.Opening.Definition.ReservePartition
            || hold.Version == long.MaxValue) return null;
        var asset = intent.Opening.Asset;
        var reserveId = PlatformReserveLedger.RecordIdFor(asset.ChainType, asset.Network.ToString(), asset.AssetId);
        var reserve = await executor.QuerySingleAsync<PlatformReserveLedger>(SurrealQuery<PlatformReserveLedger>.Key(reserveId), ct);
        if (reserve is null || reserve.Chain != asset.ChainType || reserve.Network != asset.Network.ToString()
            || reserve.AssetId != asset.AssetId || reserve.ReserveAddress != asset.HoldingAddress
            || reserve.ProtocolIdentity != asset.ProtocolIdentity) return null;
        var planned = HoldReserveMutationPlan.Create(reserve, hold.ReservePartition, ReserveDrawKind.CancelHold,
            hold.Amount, asset.ProtocolIdentity);
        if (planned.IsError || planned.Result is not { } mutation) return null;
        var mandate = hold.ActiveReleaseMandateId is { } mandateId
            ? await executor.QuerySingleAsync<HoldReleaseMandate>(SurrealQuery<HoldReleaseMandate>.Key(mandateId), ct) : null;
        if (hold.ActiveReleaseMandateId is not null && (mandate is null || mandate.Status != "Active" || mandate.Version == long.MaxValue
            || mandate.HoldRecordId != hold.Id || mandate.ExchangeSourceJson is null
            || JsonSerializer.Deserialize<HoldReleaseExchangeSource>(mandate.ExchangeSourceJson)?.SourceId != source.Id)) return null;
        var now = DateTimeOffset.UtcNow;
        var receipt = new ExchangeHoldSettlementReceipt { Id = source.Id, SourceIntentHash = source.IntentHash,
            PreparedHash = evidence.PreparedHash, EvidenceJson = source.SettlementEvidenceJson,
            HoldRecordId = hold.Id, CreatedAt = now };
        var sourceUpdate = SurrealWriter.UpdateOnly<ExchangeHoldSource>(source.Id)
            .Where(row => row.Version == source.Version && row.State == "ExpiredAbsent" && row.IntentJson == source.IntentJson
                && row.PreparedWireJson == source.PreparedWireJson && row.SettlementEvidenceJson == source.SettlementEvidenceJson)
            .Set(row => row.State, "Compensated").Set(row => row.Version, source.Version + 1).Set(row => row.UpdatedAt, now).Build();
        var holdUpdate = SurrealWriter.UpdateOnly<HoldLedger>(hold.Id)
            .Where(row => row.Version == hold.Version && row.State == "AwaitingSourceSettlement" && row.EconomicSourceId == source.Id
                && row.DefinitionHash == hold.DefinitionHash && row.OpeningCommandId == source.HoldCommandId
                && row.ActiveReleaseMandateId == hold.ActiveReleaseMandateId)
            .Set(row => row.State, "Cancelled").Set(row => row.Version, hold.Version + 1).Set(row => row.UpdatedAt, now)
            .Unset(row => row.ActiveReleaseMandateId)
            .Set(row => row.SettledAt, now).Set(row => row.SettlementReason, "Original exchange group proved absent after its full validity window.").Build();
        var reserveUpdate = SurrealWriter.UpdateOnly<PlatformReserveLedger>(reserve.Id)
            .Where(row => row.Version == reserve.Version && row.Chain == reserve.Chain && row.Network == reserve.Network
                && row.ProtocolIdentity == reserve.ProtocolIdentity && row.AssetId == reserve.AssetId
                && row.ReserveAddress == reserve.ReserveAddress && row.TenantId == reserve.TenantId
                && row.TotalSupply == reserve.TotalSupply && row.Decimals == reserve.Decimals && row.UnitName == reserve.UnitName)
            .Set(row => row.Version, reserve.Version + 1).Set(row => row.Partitions, mutation.Partitions).Set(row => row.UpdatedAt, now).Build();
        // raw: no-effect source proof, exact original liability cancellation and reserve return share one permanent receipt.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN"),
            SurrealGeneratedStatementScope.Assign(sourceUpdate, "exchange_absent_source", "_exchange_absent_source"),
            SurrealQuery.Of("IF $_exchange_absent_source = NONE OR $_exchange_absent_source = [] { THROW 'HOLD_COMMAND_CHANGED' }"),
            SurrealGeneratedStatementScope.Assign(holdUpdate, "exchange_absent_hold", "_exchange_absent_hold"),
            SurrealQuery.Of("IF $_exchange_absent_hold = NONE OR $_exchange_absent_hold = [] { THROW 'HOLD_COMMAND_CHANGED' }"),
            SurrealGeneratedStatementScope.Assign(reserveUpdate, "exchange_absent_reserve", "_exchange_absent_reserve"),
            SurrealQuery.Of("IF $_exchange_absent_reserve = NONE OR $_exchange_absent_reserve = [] { THROW 'HOLD_COMMAND_CHANGED' }") };
        if (mandate is not null)
        {
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldReleaseMandate>(mandate.Id)
                .Where(row => row.Version == mandate.Version && row.Status == "Active" && row.ExchangeSourceJson == mandate.ExchangeSourceJson)
                .Set(row => row.Status, "Cancelled").Set(row => row.Version, mandate.Version + 1).Set(row => row.UpdatedAt, now)
                .Set(row => row.Detail, "The original exchange was authoritatively absent; its release permission is closed.").Build(),
                "exchange_absent_mandate", "_exchange_absent_mandate"));
            statements.Add(SurrealQuery.Of("IF $_exchange_absent_mandate = NONE OR $_exchange_absent_mandate = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        }
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(receipt), "exchange_absent_receipt"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct);
                response.EnsureAllOk();
                return response;
            }, ct);
            return receipt;
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            prior = await executor.QuerySingleAsync<ExchangeHoldSettlementReceipt>(SurrealQuery<ExchangeHoldSettlementReceipt>.Key(sourceId), ct);
            return prior is not null && SameExchangeSettlement(prior, source, evidence) ? prior : null;
        }
    }
}
