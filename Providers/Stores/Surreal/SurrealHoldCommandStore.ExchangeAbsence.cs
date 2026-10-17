using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Reserve;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldCommandStore
{
    /// <inheritdoc/>
    public Task<ExchangeHoldSettlementReceipt?> CompensateExchangeAbsenceAsync(string sourceId, CancellationToken ct = default)
        => TerminalAsync(sourceId, "Compensated", async (source, token) =>
        {
            var evidence = TerminalEvidence(source, "ExpiredAbsent");
            if (evidence is null || source.State != "ExpiredAbsent" || source.Version == long.MaxValue) return null;
            return await PlanCommitmentReleaseAsync(source, JsonSerializer.Deserialize<ExchangeHoldSourceIntent>(source.IntentJson)!,
                evidence, "Compensated", "Original exchange group proved absent after its full validity window.", null, token);
        }, ct);

    /// <inheritdoc/>
    public Task<ExchangeHoldSettlementReceipt?> AbandonUnsentExchangeAsync(string sourceId, DateTimeOffset staleBefore,
        CancellationToken ct = default)
        => TerminalAsync(sourceId, "Abandoned", async (source, token) =>
        {
            var context = ReadExchangeSource(source);
            if (context is null || source.State is not ("Committed" or "Preparing" or "Prepared") || source.SettlementEvidenceJson is not null
                || source.UpdatedAt > staleBefore || source.Version is < 0 or long.MaxValue
                || source.HoldCommandId != (context.Intent.Opening is null ? null : HoldCommandIdentity.Operation(context.Intent.Opening))) return null;
            var prepared = source.PreparedWireJson is { } wire ? JsonSerializer.Deserialize<PreparedExchangeWire>(wire) : null;
            var asset = context.Intent.Opening?.Asset ?? context.Intent.Units!.Asset;
            var evidence = new ExchangeWireObservation("Unsent", prepared?.PreparationId ?? string.Empty, prepared?.GroupId ?? string.Empty,
                source.PreparedHash ?? string.Empty, asset.ProtocolIdentity);
            return await PlanCommitmentReleaseAsync(source, context.Intent, evidence, "Abandoned",
                "The original exchange was never authorized for transmission and was abandoned after its commitment window.", staleBefore, token);
        }, ct);

    /// <summary>The exact inverse of an unsettled commitment, joined to the source's terminal transition and one permanent receipt.</summary>
    private async Task<TerminalPlan?> PlanCommitmentReleaseAsync(ExchangeHoldSource source, ExchangeHoldSourceIntent intent,
        ExchangeWireObservation evidence, string target, string reason, DateTimeOffset? staleBefore, CancellationToken ct)
    {
        var evidenceJson = source.SettlementEvidenceJson ?? JsonSerializer.Serialize(evidence);
        var now = DateTimeOffset.UtcNow;
        var receipt = new ExchangeHoldSettlementReceipt { Id = source.Id, SourceIntentHash = source.IntentHash,
            PreparedHash = evidence.PreparedHash, EvidenceJson = evidenceJson, CreatedAt = now };
        var statements = SourceTransition(source, source.State, target, now, "exchange_release_source",
            source.SettlementEvidenceJson is null ? evidenceJson : null, staleBefore);
        if (intent.Units is { } units)
        {
            var movements = await UnitsReleaseAsync(intent, units, now, receipt, ct);
            if (movements is null) return null;
            statements.AddRange(movements);
            return new(statements, receipt);
        }
        var opening = intent.Opening!;
        var hold = await GetHoldAsync(source.HoldRecordId!, ct);
        if (hold is null || hold.EconomicSourceId != source.Id || hold.State != "AwaitingSourceSettlement"
            || hold.OpeningCommandId != source.HoldCommandId || opening.Definition is null || hold.DefinitionHash != CanonicalJson.Hash(opening.Definition)
            || hold.Amount != opening.Definition.QuantityBaseUnits || hold.ReservePartition != opening.Definition.ReservePartition
            || hold.Version == long.MaxValue) return null;
        var reserve = await ExchangeReserveAsync(opening.Asset, ct);
        if (reserve is null) return null;
        var planned = HoldReserveMutationPlan.Create(reserve, hold.ReservePartition, ReserveDrawKind.CancelHold,
            hold.Amount, opening.Asset.ProtocolIdentity);
        if (planned.IsError || planned.Result is not { } mutation) return null;
        var mandate = hold.ActiveReleaseMandateId is { } mandateId
            ? await executor.QuerySingleAsync<HoldReleaseMandate>(SurrealQuery<HoldReleaseMandate>.Key(mandateId), ct) : null;
        if (hold.ActiveReleaseMandateId is not null && (mandate is null || mandate.Status != "Active" || mandate.Version == long.MaxValue
            || SurrealRecordGuid.BareId(mandate.HoldRecordId) != SurrealRecordGuid.BareId(hold.Id) || mandate.ExchangeSourceJson is null
            || JsonSerializer.Deserialize<HoldReleaseExchangeSource>(mandate.ExchangeSourceJson)?.SourceId != source.Id)) return null;
        receipt.HoldRecordId = hold.Id;
        receipt.ReserveRecordId = reserve.Id;
        receipt.MovementJson = JsonSerializer.Serialize(new { Hold = "Cancelled", Reserve = $"CancelHold:{hold.Amount}" });
        // raw: no-effect or never-sent source proof, exact original liability cancellation and reserve return share one permanent receipt.
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldLedger>(hold.Id)
            .Where(row => row.Version == hold.Version && row.State == "AwaitingSourceSettlement" && row.EconomicSourceId == source.Id
                && row.DefinitionHash == hold.DefinitionHash && row.OpeningCommandId == source.HoldCommandId
                && row.ActiveReleaseMandateId == hold.ActiveReleaseMandateId)
            .Set(row => row.State, "Cancelled").Set(row => row.Version, hold.Version + 1).Set(row => row.UpdatedAt, now)
            .Unset(row => row.ActiveReleaseMandateId).Set(row => row.SettledAt, now).Set(row => row.SettlementReason, reason).Build(),
            "exchange_release_hold", "_exchange_release_hold"));
        statements.Add(SurrealGeneratedStatementScope.RequireResult("_exchange_release_hold", Conflict));
        statements.AddRange(ReserveWrite(reserve, mutation, now, "exchange_release_reserve"));
        if (mandate is not null)
            statements.AddRange(CloseMandate(mandate, now, "The original exchange never settled; its release permission is closed.",
                "exchange_release_mandate"));
        return new(statements, receipt);
    }
}
