using System.Text.Json;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Helpers;

/// <summary>Validates exact persisted liability and separately bounded native cost categories.</summary>
public static class HoldReleaseMandateIdentity
{
    public static string Id(Guid actor, Guid? tenant, Guid requestId)
        => CanonicalJson.Hash(new { Domain = "hold-release-mandate-v1", Actor = actor, Tenant = tenant, Request = requestId });

    public static bool ValidTerms(HoldLedger hold, HoldReleaseMandateRequest request)
    {
        var terms = request.Terms; var quote = request.Quote;
        if (terms is null || terms.Asset is null || quote is null || request.RequestId == Guid.Empty || request.HoldVersion != hold.Version
            || hold.State != "Held" || hold.ActiveCommandId is not null || hold.ActiveReleaseMandateId is not null
            || hold.DefinitionJson is null || hold.DefinitionHash != CanonicalJson.HashCanonical(hold.DefinitionJson)
            || hold.OpeningCommandId is null || terms.Version != 1 || terms.HoldRecordId != SurrealRecordGuid.BareId(hold.Id)
            || terms.DefinitionHash != hold.DefinitionHash || terms.QuantityBaseUnits != hold.Amount
            || terms.Recipient is null || terms.Recipient.Id == Guid.Empty
            || terms.Recipient.OwnerUserId != SurrealRecordGuid.Parse(hold.BeneficiaryUserId)
            || terms.Recipient.ChainType != hold.Chain || terms.NotBefore >= terms.ExpiresAt
            || (hold.ReleaseNotBefore is { } cutoff && terms.NotBefore < cutoff)
            || !ValueCustodyIdentity.ValidQuote(quote) || quote.TransferCosts is null
            || terms.NativeCurrency != quote.NativeCurrency || terms.NativeDecimals != quote.NativeDecimals
            || quote.ProtocolIdentity != terms.Asset.ProtocolIdentity || quote.Request.Network.ToString() != hold.Network
            || quote.Request.ChainType != hold.Chain || quote.Request.AssetId != hold.AssetId
            || quote.Request.Sender != hold.HoldingAddress || quote.Request.Recipient != terms.Recipient.Address
            || quote.Request.QuantityBaseUnits != hold.Amount || quote.Request.HoldingAddress != hold.HoldingAddress) return false;
        var definition = JsonSerializer.Deserialize<HoldDefinition>(hold.DefinitionJson);
        return definition is not null && terms.Asset == definition.Asset && CostsWithin(quote, terms.MaximumCosts);
    }

    public static bool CostsWithin(ValueCustodyQuote quote, ValueCustodyCostBreakdown maximum)
    {
        var actual = quote.TransferCosts;
        return actual is not null && maximum is not null && actual.PayerAddress == quote.Request.Sender
            && actual.RefundRecipientAddress == quote.Request.Sender && maximum.PayerAddress == actual.PayerAddress
            && maximum.RefundRecipientAddress == actual.RefundRecipientAddress
            && actual.NetworkFeeBaseUnits == quote.TransferNetworkFeeBaseUnits
            && Within(actual.NetworkFeeBaseUnits, maximum.NetworkFeeBaseUnits)
            && Within(actual.LockedBalanceBaseUnits, maximum.LockedBalanceBaseUnits)
            && Within(actual.NonrefundableProtocolCostBaseUnits, maximum.NonrefundableProtocolCostBaseUnits);
    }

    private static bool Within(string actual, string maximum)
        => UnsignedIntegerText.TryParse(actual, out var cost)
            && UnsignedIntegerText.TryParse(string.IsNullOrEmpty(maximum) ? "0" : maximum, out var cap) && cost <= cap;
}
