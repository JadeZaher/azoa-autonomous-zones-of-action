using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

internal static class HoldReleaseExchangeFence
{
    internal static IReadOnlyList<SurrealQuery>? Create(HoldReleaseMandate mandate)
    {
        if (mandate.ExchangeSourceJson is null || mandate.WorkflowSourceJson is not null) return null;
        var source = JsonSerializer.Deserialize<HoldReleaseExchangeSource>(mandate.ExchangeSourceJson);
        if (source is null || source.Version != 1 || source.SourceId.Length != 64 || source.SourceIntentHash.Length != 64
            || source.OpeningOutcomeHash.Length != 64 || source.Authority.Tenant.Credential.ActorUserId.ToString("N") != mandate.ActorUserId
            || mandate.TenantId is not null
            || source.Lineage is { } lineage && (string.IsNullOrWhiteSpace(lineage.OriginalHoldRecordId)
                || string.IsNullOrWhiteSpace(lineage.RecoveryCommandId) || lineage.RecoveryOutcomeHash.Length != 64)) return null;
        var original = ExchangeAuthorityFence.Create(source.Authority, "hold_exchange_original");
        if (original is null) return null;
        var result = original.ToList();
        var authorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(source.Authority));
        var sourceHoldRecordId = source.Lineage?.OriginalHoldRecordId ?? mandate.HoldRecordId;
        var liabilityOpening = source.Lineage?.RecoveryCommandId ?? source.OpeningCommandId;
        result.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<ExchangeHoldSource>(source.SourceId)
            .Where(row => row.State == "Completed" && row.IntentHash == source.SourceIntentHash && row.AuthorityJson == authorityJson
                && row.HoldRecordId == sourceHoldRecordId && row.HoldCommandId == source.OpeningCommandId)
            .Set(row => row.State, "Completed").Build(), "hold_exchange_source", "_hold_exchange_source"));
        // lock: settlement and command receipts are create-only; the source CAS above serializes this release.
        result.Add(SurrealGeneratedStatementScope.AssignRecord(SurrealQuery<ExchangeHoldSettlementReceipt>.Key(source.SourceId),
            "hold_exchange_settlement", "_hold_exchange_settlement"));
        result.Add(SurrealGeneratedStatementScope.AssignRecord(SurrealQuery<HoldCommandReceipt>.Key(source.OpeningCommandId),
            "hold_exchange_opening", "_hold_exchange_opening"));
        result.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldLedger>(mandate.HoldRecordId)
            .Where(row => row.EconomicSourceId == source.SourceId && row.OpeningCommandId == liabilityOpening
                && row.ActiveReleaseMandateId == mandate.Id && (row.State == "Held" || row.State == "Releasing"))
            .Set(row => row.EconomicSourceId, source.SourceId).Build(), "hold_exchange_liability", "_hold_exchange_liability"));
        // raw: only the original fully settled purchase and exact immutable receipts may authorize its future custody release.
        result.Add(SurrealQuery.Of("""
            IF $_hold_exchange_source = NONE OR $_hold_exchange_source = []
                OR $_hold_exchange_settlement = NONE OR $_hold_exchange_opening = NONE
                OR $_hold_exchange_liability = NONE OR $_hold_exchange_liability = []
                OR $_hold_exchange_settlement.source_intent_hash != $_hold_exchange_source.intent_hash
                OR $_hold_exchange_settlement.hold_record_id != $_hold_exchange_source.hold_record_id
                OR $_hold_exchange_settlement.evidence_json != $_hold_exchange_source.settlement_evidence_json
                OR $_hold_exchange_settlement.prepared_hash != $_hold_exchange_source.prepared_hash
                OR crypto::sha256($_hold_exchange_opening.outcome_json) != $_hold_exchange_opening_hash
                { THROW 'SIGNING_AUTHORITY_CHANGED' }
            """).WithParam("_hold_exchange_opening_hash", source.OpeningOutcomeHash));
        if (source.Lineage is { } split)
        {
            // lock: the recovery receipt is create-only; the liability CAS above serializes this release.
            result.Add(SurrealGeneratedStatementScope.AssignRecord(SurrealQuery<HoldCommandReceipt>.Key(split.RecoveryCommandId),
                "hold_exchange_lineage", "_hold_exchange_lineage"));
            // raw: a narrowed remainder permission requires the exact immutable recovery receipt that created its liability.
            result.Add(SurrealQuery.Of("""
                IF $_hold_exchange_lineage = NONE OR crypto::sha256($_hold_exchange_lineage.outcome_json) != $_hold_exchange_lineage_hash
                    { THROW 'SIGNING_AUTHORITY_CHANGED' }
                """).WithParam("_hold_exchange_lineage_hash", split.RecoveryOutcomeHash));
        }
        return result;
    }
}
