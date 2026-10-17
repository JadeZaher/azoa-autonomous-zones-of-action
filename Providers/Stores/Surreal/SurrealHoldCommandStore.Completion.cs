using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldCommandStore
{
    /// <inheritdoc/>
    public async Task<HoldCommandReceipt?> CompleteRailAsync(HoldCommand expected, HoldTransferEvidence evidence,
        CancellationToken ct = default)
    {
        var prior = await GetReceiptAsync(expected.Id, ct);
        if (prior is not null) return SameFinalReceipt(prior, expected, evidence) ? prior : null;
        if (expected.State != "Submitted") return null;
        var intent = JsonSerializer.Deserialize<HoldCommandIntent>(expected.IntentJson)!;
        var originalAuthority = JsonSerializer.Deserialize<HoldCommandAuthority>(expected.AuthorityJson)!;
        HoldReleaseMandate? mandate = null;
        if (originalAuthority.ReleaseMandateId is { } mandateId)
        {
            mandate = await executor.QuerySingleAsync<HoldReleaseMandate>(SurrealQuery<HoldReleaseMandate>.Key(mandateId), ct);
            if (mandate is null || mandate.OperationId != expected.Id || mandate.HoldRecordId != expected.HoldRecordId
                || mandate.Version is < 0 or long.MaxValue) return null;
        }
        var aggregate = await GetHoldAsync(expected.HoldRecordId, ct);
        var reserveId = PlatformReserveLedger.RecordIdFor(intent.Asset.ChainType, intent.Asset.Network.ToString(), intent.Asset.AssetId);
        var reserve = await executor.QuerySingleAsync<PlatformReserveLedger>(SurrealQuery<PlatformReserveLedger>.Key(reserveId), ct);
        if (aggregate is null || reserve is null || reserve.Version is < 0 or long.MaxValue) return null;
        var planned = HoldRailCompletionPlan.Create(expected, evidence, aggregate, reserve);
        if (planned.IsError || planned.Result is not { } plan)
        {
            prior = await GetReceiptAsync(expected.Id, ct);
            return prior is not null && SameFinalReceipt(prior, expected, evidence) ? prior : null;
        }
        // raw: finalized exact-wire evidence, liability, reserve counters and immutable receipt commit without new signing authority.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        if (mandate is not null)
        {
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldReleaseMandate>(mandate.Id)
                .Where(row => row.Version == mandate.Version && row.OperationId == expected.Id
                    && row.HoldRecordId == expected.HoldRecordId && row.TermsHash == mandate.TermsHash)
                .Set(row => row.Version, mandate.Version + 1).Set(row => row.Status, "Completed")
                .Set(row => row.Detail, "The original authorized release finalized and its custody accounting was recorded.")
                .Set(row => row.UpdatedAt, evidence.ObservedAt).Build(), "hold_final_mandate", "_hold_final_mandate"));
            statements.Add(SurrealQuery.Of("IF $_hold_final_mandate = NONE OR $_hold_final_mandate = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        }
        var reserveWrite = SurrealWriter.UpdateOnly<PlatformReserveLedger>(reserveId)
            .Where(row => row.Version == reserve.Version && row.Chain == reserve.Chain && row.Network == reserve.Network
                && row.ProtocolIdentity == reserve.ProtocolIdentity && row.AssetId == reserve.AssetId
                && row.ReserveAddress == reserve.ReserveAddress && row.TenantId == reserve.TenantId
                && row.TotalSupply == reserve.TotalSupply && row.Decimals == reserve.Decimals && row.UnitName == reserve.UnitName)
            .Set(row => row.Version, plan.Reserve is null ? reserve.Version : reserve.Version + 1);
        if (plan.Reserve is { } mutation)
            reserveWrite = reserveWrite.Set(row => row.Partitions, mutation.Partitions).Set(row => row.UpdatedAt, evidence.ObservedAt);
        statements.Add(SurrealGeneratedStatementScope.Assign(reserveWrite.Build(), "hold_final_reserve", "_hold_final_reserve"));
        statements.Add(SurrealQuery.Of("IF $_hold_final_reserve = NONE OR $_hold_final_reserve = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        var liabilityWrite = SurrealWriter.UpdateOnly<HoldLedger>(aggregate.Id)
            .Where(row => row.Version == aggregate.Version && row.ActiveCommandId == expected.Id && row.State == aggregate.State
                && row.DefinitionJson == aggregate.DefinitionJson && row.DefinitionHash == aggregate.DefinitionHash
                && row.OpeningCommandId == aggregate.OpeningCommandId && row.OwnerUserId == aggregate.OwnerUserId
                && row.TenantId == aggregate.TenantId)
            .Set(row => row.Version, plan.Aggregate.Version).Set(row => row.State, plan.Aggregate.State)
            .Set(row => row.UpdatedAt, evidence.ObservedAt).Set(row => row.SettlementTransactionId, evidence.TransactionId)
            .Unset(row => row.ActiveCommandId).Unset(row => row.ActiveReleaseMandateId);
        if (plan.Aggregate.SettledAt is { } settled) liabilityWrite = liabilityWrite.Set(row => row.SettledAt, settled);
        if (plan.Aggregate.SettlementReason is { } reason) liabilityWrite = liabilityWrite.Set(row => row.SettlementReason, reason);
        statements.Add(SurrealGeneratedStatementScope.Assign(liabilityWrite.Build(), "hold_final_liability", "_hold_final_liability"));
        statements.Add(SurrealQuery.Of("IF $_hold_final_liability = NONE OR $_hold_final_liability = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldCommand>(expected.Id)
            .Where(row => row.Version == expected.Version && row.State == "Submitted" && row.IntentHash == expected.IntentHash
                && row.PreparedJson == expected.PreparedJson && row.StageIntentJson == expected.StageIntentJson
                && row.RailPlanJson == expected.RailPlanJson && row.Generation == expected.Generation)
            .Set(row => row.Version, expected.Version + 1).Set(row => row.State, "Completed")
            .Set(row => row.ObservationJson, plan.Receipt.EvidenceJson!).Set(row => row.UpdatedAt, evidence.ObservedAt).Build(),
            "hold_final_command", "_hold_final_command"));
        statements.Add(SurrealQuery.Of("IF $_hold_final_command = NONE OR $_hold_final_command = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(plan.Receipt), "hold_final_receipt"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct);
                response.EnsureAllOk(); return response;
            }, ct);
            return plan.Receipt;
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(Conflict, StringComparison.Ordinal))
        {
            prior = await GetReceiptAsync(expected.Id, ct);
            return prior is not null && SameFinalReceipt(prior, expected, evidence) ? prior : null;
        }
    }

    private static bool SameFinalReceipt(HoldCommandReceipt receipt, HoldCommand command, HoldTransferEvidence evidence)
    {
        if (receipt.IntentHash != command.IntentHash || receipt.ActorUserId != command.ActorUserId
            || receipt.TenantId != command.TenantId || receipt.HoldRecordId != command.HoldRecordId || receipt.EvidenceJson is null) return false;
        var original = JsonSerializer.Deserialize<HoldTransferEvidence>(receipt.EvidenceJson)!;
        return original.Version == 1 && original.Confirmation == ChainConfirmation.Confirmed
            && evidence.Confirmation == original.Confirmation && original.PreparedHash == evidence.PreparedHash
            && original.TransactionId == evidence.TransactionId && original.ProtocolIdentity == evidence.ProtocolIdentity;
    }
}
