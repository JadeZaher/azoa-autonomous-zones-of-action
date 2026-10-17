using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
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
            || mandate.TenantId is not null) return null;
        var original = ExchangeAuthorityFence.Create(source.Authority, "hold_exchange_original");
        if (original is null) return null;
        var result = original.ToList();
        var authorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(source.Authority));
        result.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<ExchangeHoldSource>(source.SourceId)
            .Where(row => row.State == "Completed" && row.IntentHash == source.SourceIntentHash && row.AuthorityJson == authorityJson
                && row.HoldRecordId == mandate.HoldRecordId && row.HoldCommandId == source.OpeningCommandId)
            .Set(row => row.State, "Completed").Build(), "hold_exchange_source", "_hold_exchange_source"));
        result.Add(SurrealGeneratedStatementScope.Assign(SurrealQuery<ExchangeHoldSettlementReceipt>.Key(source.SourceId),
            "hold_exchange_settlement", "_hold_exchange_settlement"));
        result.Add(SurrealGeneratedStatementScope.Assign(SurrealQuery<HoldCommandReceipt>.Key(source.OpeningCommandId),
            "hold_exchange_opening", "_hold_exchange_opening"));
        result.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldLedger>(mandate.HoldRecordId)
            .Where(row => row.EconomicSourceId == source.SourceId && row.OpeningCommandId == source.OpeningCommandId
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
        return result;
    }
}
