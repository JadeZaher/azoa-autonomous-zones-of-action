using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Signing;

public sealed class ValueCustodyIdentityTests
{
    [Fact]
    public void AbsentCostPreservesHistoricalValueIntentSerialization()
    {
        var signing = SigningContext.Platform;
        var intent = new ValueTransferIntent("Algorand", ChainNetwork.Devnet, "42", "sender", "recipient", "5", signing, "key");
        var original = new { intent.ChainType, intent.Network, intent.AssetId, intent.Sender, intent.Recipient,
            intent.QuantityBaseUnits, intent.SigningContext, intent.PreparationKey };
        JsonSerializer.Serialize(intent).Should().Be(JsonSerializer.Serialize(original));
        CanonicalJson.Hash(intent).Should().Be(CanonicalJson.Hash(original));
    }

    [Fact]
    public void QuoteIdentityBindsPhysicalNetworkNativeCostAndEachParticipant()
    {
        var request = new ValueCustodyQuoteRequest("Algorand", ChainNetwork.Devnet, "42", "sender", "recipient", "5", "reserve");
        var quote = new ValueCustodyQuote(1, "", request, "genesis", "authority", "ALGO", 6,
            "1000", false, "1000", "100000", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));
        quote = quote with { QuoteId = ValueCustodyIdentity.QuoteId(quote) };
        ValueCustodyIdentity.ValidQuote(quote).Should().BeTrue();
        ValueCustodyIdentity.ValidQuote(quote with { NativeDecimals = 9 }).Should().BeFalse();
        ValueCustodyIdentity.ValidQuote(quote with { ProtocolIdentity = "other" }).Should().BeFalse();
        var cost = new ValueTransferCostAuthorization(1, "command", "release", 0, quote, "sender", "1000");
        var transfer = new ValueTransferIntent("Algorand", ChainNetwork.Devnet, "42", "sender", "recipient", "5",
            SigningContext.Platform, "key", cost);
        ValueCustodyIdentity.MatchesTransfer(transfer).Should().BeTrue();
        ValueCustodyIdentity.MatchesTransfer(transfer with { QuantityBaseUnits = "6" }).Should().BeFalse();
        ValueCustodyIdentity.MatchesTransfer(transfer with { Cost = cost with { PayerAddress = "recipient" } }).Should().BeFalse();
        ValueCustodyIdentity.MatchesTransfer(transfer with { Cost = cost with { MaximumNetworkFeeBaseUnits = "999" } }).Should().BeFalse();
        var acceptance = new ValueAcceptanceIntent("Algorand", ChainNetwork.Devnet, "42", "recipient", "accept-key",
            new SigningContext(Guid.NewGuid(), Guid.NewGuid(), false, Scope: AzoaScopes.TransferSign),
            cost with { Stage = "accept", PayerAddress = "recipient" });
        ValueCustodyIdentity.MatchesAcceptance(acceptance).Should().BeTrue();
        ValueCustodyIdentity.MatchesAcceptance(acceptance with { Recipient = "sender" }).Should().BeFalse();
    }
}
