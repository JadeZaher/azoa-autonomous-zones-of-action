using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Reserve;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldCommandStore
{
    /// <inheritdoc/>
    public Task<ExchangeHoldSettlementReceipt?> CompleteExchangeSettlementAsync(string sourceId, CancellationToken ct = default)
        => TerminalAsync(sourceId, "Completed", PlanSettlementAsync, ct);

    private async Task<TerminalPlan?> PlanSettlementAsync(ExchangeHoldSource source, CancellationToken ct)
    {
        var evidence = TerminalEvidence(source, "Confirmed");
        if (evidence is null || source.State != "Confirmed" || source.Version == long.MaxValue
            || !UnsignedIntegerText.TryParse(evidence.FinalizedPosition, out var round) || round <= 0) return null;
        var intent = JsonSerializer.Deserialize<ExchangeHoldSourceIntent>(source.IntentJson)!;
        if (intent.Units is { } units) return await PlanUnitsSettlementAsync(source, intent, units, evidence, ct);
        var opening = intent.Opening!;
        var hold = await GetHoldAsync(source.HoldRecordId!, ct);
        var treasury = await ExchangeTreasuryAsync(intent, opening.Asset, ct);
        if (hold is null || treasury is null || hold.EconomicSourceId != source.Id || hold.State != "AwaitingSourceSettlement"
            || hold.OpeningCommandId != source.HoldCommandId || opening.Definition is null || hold.DefinitionHash != CanonicalJson.Hash(opening.Definition)
            || hold.Version == long.MaxValue) return null;
        var now = DateTimeOffset.UtcNow;
        var credit = TreasuryMovement(treasury, intent.PlatformAssetId, ProjectTreasuryMovementKind.Credit,
            intent.PlatformQuantityBaseUnits, now, "exchange_final_treasury");
        if (credit is null) return null;
        // raw: finalized source, reserve liability activation, treasury credit and immutable receipt commit exactly once.
        var statements = SourceTransition(source, "Confirmed", "Completed", now, "exchange_final_source");
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldLedger>(hold.Id)
            .Where(row => row.Version == hold.Version && row.State == "AwaitingSourceSettlement" && row.EconomicSourceId == source.Id
                && row.DefinitionHash == hold.DefinitionHash && row.OpeningCommandId == source.HoldCommandId)
            .Set(row => row.State, "Held").Set(row => row.Version, hold.Version + 1).Set(row => row.UpdatedAt, now).Build(),
            "exchange_final_hold", "_exchange_final_hold"));
        statements.Add(SurrealGeneratedStatementScope.RequireResult("_exchange_final_hold", Conflict));
        statements.AddRange(credit);
        return new(statements, new ExchangeHoldSettlementReceipt { Id = source.Id, SourceIntentHash = source.IntentHash,
            PreparedHash = evidence.PreparedHash, EvidenceJson = source.SettlementEvidenceJson!, HoldRecordId = hold.Id,
            TreasuryRecordId = treasury.Id, CreatedAt = now,
            MovementJson = JsonSerializer.Serialize(new { Hold = "Held", Treasury = $"Credit:{intent.PlatformQuantityBaseUnits}" }) });
    }

    /// <summary>Recorded finalized evidence that proves exactly the saved group, or null.</summary>
    private static ExchangeWireObservation? TerminalEvidence(ExchangeHoldSource source, string verdict)
    {
        var plan = ExchangePlan(source);
        if (plan is null || source.PreparedWireJson is null || source.SettlementEvidenceJson is null) return null;
        var prepared = JsonSerializer.Deserialize<PreparedExchangeWire>(source.PreparedWireJson)!;
        var evidence = JsonSerializer.Deserialize<ExchangeWireObservation>(source.SettlementEvidenceJson)!;
        return evidence.Verdict == verdict && evidence.PreparedHash == CanonicalJson.Hash(prepared)
            && source.PreparedHash == evidence.PreparedHash
            && evidence.PreparationId == prepared.PreparationId && evidence.GroupId == prepared.GroupId
            && evidence.ProtocolIdentity == plan.Value.Intent.ProtocolIdentity
            && CanonicalJson.Hash(prepared.Intent) == CanonicalJson.Hash(plan.Value.Intent) ? evidence : null;
    }
}
