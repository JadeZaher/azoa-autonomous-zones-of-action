using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldCommandStore
{
    private async Task<IReadOnlyList<SurrealQuery>?> MandateAuthorityAsync(HoldCommandIntent intent, HoldRailPlan plan,
        HoldCommandAuthority authority, string mandateId, CancellationToken ct)
    {
        if (authority.Version != 1 || authority.Direct is not null || authority.Workflow is not null
            || authority.EconomicSource is not null || authority.Sender is not null || intent.Kind != "Release"
            || plan.Kind != "Release" || plan.Generation != 0 || plan.SenderWallet is not null
            || !plan.SenderSigningContext.IsPlatform || intent.RequestKey != "mandate:" + mandateId) return null;
        var mandate = await executor.QuerySingleAsync<HoldReleaseMandate>(SurrealQuery<HoldReleaseMandate>.Key(mandateId), ct);
        if (mandate is null || mandate.ActorUserId != intent.ActorUserId.ToString("N")
            || mandate.TenantId != intent.TenantId?.ToString("N") || mandate.HoldRecordId != HoldCommandIdentity.Hold(intent)
            || (mandate.OperationId is not null && mandate.OperationId != plan.CommandId)) return null;
        var terms = JsonSerializer.Deserialize<HoldReleaseMandateTerms>(mandate.TermsJson);
        var originalQuote = JsonSerializer.Deserialize<ValueCustodyQuote>(mandate.OriginalQuoteJson);
        if (terms is null || originalQuote is null || !ValueCustodyIdentity.ValidQuote(plan.Quote)
            || terms.Asset != intent.Asset || terms.Recipient != plan.RecipientWallet
            || plan.Quote.Request != originalQuote.Request || plan.Quote.ProtocolIdentity != terms.Asset.ProtocolIdentity
            || plan.Quote.AssetAuthorityHash != originalQuote.AssetAuthorityHash
            || plan.Quote.NativeCurrency != terms.NativeCurrency || plan.Quote.NativeDecimals != terms.NativeDecimals
            || plan.Quote.Request.QuantityBaseUnits != terms.QuantityBaseUnits
            || !HoldReleaseMandateIdentity.CostsWithin(plan.Quote, terms.MaximumCosts)) return null;
        var guards = HoldReleaseMandateFence.Create(mandate, "hold_mandate");
        if (guards is null) return null;
        var result = guards.ToList();
        result.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldLedger>(mandate.HoldRecordId)
            .Where(row => row.ActiveReleaseMandateId == mandateId && row.State == "Held"
                && row.DefinitionHash == terms.DefinitionHash && row.Amount == terms.QuantityBaseUnits)
            .Set(row => row.ActiveReleaseMandateId, mandateId).Build(), "hold_mandate_liability", "_hold_mandate_liability"));
        // raw: delayed custody authority is usable only while its exact original liability remains reserved to this mandate.
        result.Add(SurrealQuery.Of("IF $_hold_mandate_liability = NONE OR $_hold_mandate_liability = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        return result;
    }
}
