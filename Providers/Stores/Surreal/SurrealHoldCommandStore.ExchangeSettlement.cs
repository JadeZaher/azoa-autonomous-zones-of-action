using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Reserve;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldCommandStore
{
    /// <inheritdoc/>
    public async Task<ExchangeHoldSettlementReceipt?> CompleteExchangeSettlementAsync(string sourceId, CancellationToken ct = default)
    {
        var source = await GetExchangeSourceAsync(sourceId, ct);
        var plan = ExchangePlan(source);
        if (source is null || plan is null || source.PreparedWireJson is null || source.SettlementEvidenceJson is null) return null;
        var intent = JsonSerializer.Deserialize<ExchangeHoldSourceIntent>(source.IntentJson)!;
        var prepared = JsonSerializer.Deserialize<PreparedExchangeWire>(source.PreparedWireJson)!;
        var evidence = JsonSerializer.Deserialize<ExchangeWireObservation>(source.SettlementEvidenceJson)!;
        if (evidence.Verdict != "Confirmed" || evidence.PreparedHash != CanonicalJson.Hash(prepared)
            || source.PreparedHash != evidence.PreparedHash
            || evidence.PreparationId != prepared.PreparationId || evidence.GroupId != prepared.GroupId
            || evidence.ProtocolIdentity != plan.Value.Intent.ProtocolIdentity
            || CanonicalJson.Hash(prepared.Intent) != CanonicalJson.Hash(plan.Value.Intent)
            || !UnsignedIntegerText.TryParse(evidence.FinalizedPosition, out var round) || round <= 0) return null;
        var prior = await executor.QuerySingleAsync<ExchangeHoldSettlementReceipt>(SurrealQuery<ExchangeHoldSettlementReceipt>.Key(sourceId), ct);
        if (prior is not null) return SameExchangeSettlement(prior, source, evidence) ? prior : null;
        if (source.State != "Confirmed" || source.Version == long.MaxValue) return null;
        var hold = await GetHoldAsync(source.HoldRecordId!, ct);
        if (hold is null || hold.EconomicSourceId != sourceId || hold.State != "AwaitingSourceSettlement"
            || hold.OpeningCommandId != source.HoldCommandId || hold.DefinitionHash != CanonicalJson.Hash(intent.Opening.Definition)
            || hold.Version == long.MaxValue) return null;
        var treasuryId = ProjectTreasuryLedger.RecordIdFor(intent.Opening.Asset.ChainType, intent.Opening.Asset.Network.ToString(),
            intent.TenantUserId.ToString("N"), intent.ProjectReference);
        var treasury = await executor.QuerySingleAsync<ProjectTreasuryLedger>(SurrealQuery<ProjectTreasuryLedger>.Key(treasuryId), ct);
        if (treasury is null || treasury.Chain != intent.Opening.Asset.ChainType || treasury.Network != intent.Opening.Asset.Network.ToString()
            || SurrealRecordGuid.ParseOptional(treasury.TenantId) != intent.TenantUserId || treasury.ProjectReference != intent.ProjectReference
            || treasury.TreasuryAddress != intent.TreasuryAddress || treasury.Version == long.MaxValue) return null;
        var assets = ProjectTreasuryAssetCodec.Decode(treasury.Assets, out _);
        if (assets is null || !UnsignedIntegerText.TryParse(intent.PlatformQuantityBaseUnits, out var quantity)) return null;
        var counters = assets.GetValueOrDefault(intent.PlatformAssetId) ?? ProjectTreasuryAssetCounters.Empty;
        if (!counters.TryApply(ProjectTreasuryMovementKind.Credit, quantity, intent.PlatformAssetId, out var credited, out _)) return null;
        assets[intent.PlatformAssetId] = credited;
        var now = DateTimeOffset.UtcNow;
        var receipt = new ExchangeHoldSettlementReceipt { Id = source.Id, SourceIntentHash = source.IntentHash,
            PreparedHash = evidence.PreparedHash, EvidenceJson = source.SettlementEvidenceJson, HoldRecordId = hold.Id,
            TreasuryRecordId = treasury.Id, CreatedAt = now };
        var sourceUpdate = SurrealWriter.UpdateOnly<ExchangeHoldSource>(source.Id)
            .Where(row => row.Version == source.Version && row.State == "Confirmed" && row.IntentJson == source.IntentJson
                && row.PreparedWireJson == source.PreparedWireJson && row.SettlementEvidenceJson == source.SettlementEvidenceJson)
            .Set(row => row.State, "Completed").Set(row => row.Version, source.Version + 1).Set(row => row.UpdatedAt, now).Build();
        var holdUpdate = SurrealWriter.UpdateOnly<HoldLedger>(hold.Id)
            .Where(row => row.Version == hold.Version && row.State == "AwaitingSourceSettlement" && row.EconomicSourceId == source.Id
                && row.DefinitionHash == hold.DefinitionHash && row.OpeningCommandId == source.HoldCommandId)
            .Set(row => row.State, "Held").Set(row => row.Version, hold.Version + 1).Set(row => row.UpdatedAt, now).Build();
        var treasuryUpdate = SurrealWriter.UpdateOnly<ProjectTreasuryLedger>(treasury.Id)
            .Where(row => row.Version == treasury.Version && row.TreasuryAddress == intent.TreasuryAddress)
            .Set(row => row.Assets, ProjectTreasuryAssetCodec.Encode(assets)).Set(row => row.Version, treasury.Version + 1)
            .Set(row => row.UpdatedAt, now).Build();
        // raw: finalized source, reserve liability activation, treasury credit and immutable receipt commit exactly once.
        var query = SurrealQuery.Combine(SurrealQuery.Of("BEGIN"),
            SurrealGeneratedStatementScope.Assign(sourceUpdate, "exchange_final_source", "_exchange_final_source"),
            SurrealQuery.Of("IF $_exchange_final_source = NONE OR $_exchange_final_source = [] { THROW 'HOLD_COMMAND_CHANGED' }"),
            SurrealGeneratedStatementScope.Assign(holdUpdate, "exchange_final_hold", "_exchange_final_hold"),
            SurrealQuery.Of("IF $_exchange_final_hold = NONE OR $_exchange_final_hold = [] { THROW 'HOLD_COMMAND_CHANGED' }"),
            SurrealGeneratedStatementScope.Assign(treasuryUpdate, "exchange_final_treasury", "_exchange_final_treasury"),
            SurrealQuery.Of("IF $_exchange_final_treasury = NONE OR $_exchange_final_treasury = [] { THROW 'HOLD_COMMAND_CHANGED' }"),
            SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(receipt), "exchange_final_receipt"), SurrealQuery.Of("COMMIT"));
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

    private static bool SameExchangeSettlement(ExchangeHoldSettlementReceipt receipt, ExchangeHoldSource source, ExchangeWireObservation evidence)
        => receipt.Id == source.Id && receipt.SourceIntentHash == source.IntentHash && receipt.PreparedHash == evidence.PreparedHash
            && receipt.EvidenceJson == source.SettlementEvidenceJson && receipt.HoldRecordId == source.HoldRecordId;
}
