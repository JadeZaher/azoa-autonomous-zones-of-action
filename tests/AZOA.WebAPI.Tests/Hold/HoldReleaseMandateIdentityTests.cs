using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Hold;

public sealed class HoldReleaseMandateIdentityTests
{
    [Fact]
    public void EachCostCategoryAndRefundDestinationRequiresItsOwnAuthorization()
    {
        var now = DateTimeOffset.UtcNow;
        var actual = new ValueCustodyCostBreakdown("custody", "custody", "1000", "2000", "30");
        var quote = new ValueCustodyQuote(1, "", new("Algorand", ChainNetwork.Devnet, "42", "custody", "beneficiary", "1", "custody"),
            "genesis", "mechanics", "ALGO", 6, "1000", true, "0", "0", now, now.AddMinutes(5), actual);
        quote = quote with { QuoteId = ValueCustodyIdentity.QuoteId(quote) };
        HoldReleaseMandateIdentity.CostsWithin(quote, actual).Should().BeTrue();
        HoldReleaseMandateIdentity.CostsWithin(quote, actual with { LockedBalanceBaseUnits = "1999" }).Should().BeFalse();
        HoldReleaseMandateIdentity.CostsWithin(quote, actual with { NonrefundableProtocolCostBaseUnits = "" }).Should().BeFalse();
        HoldReleaseMandateIdentity.CostsWithin(quote, actual with { NetworkFeeBaseUnits = "" }).Should().BeFalse();
        HoldReleaseMandateIdentity.CostsWithin(quote, actual with { RefundRecipientAddress = "other" }).Should().BeFalse();
        HoldReleaseMandateIdentity.CostsWithin(quote, actual with { PayerAddress = "beneficiary" }).Should().BeFalse();
        HoldReleaseMandateIdentity.CostsWithin(quote with { TransferCosts = null }, actual).Should().BeFalse();
        HoldReleaseMandateIdentity.CostsWithin(quote with { TransferCosts = actual with { NetworkFeeBaseUnits = "999" } }, actual).Should().BeFalse();
    }
}
