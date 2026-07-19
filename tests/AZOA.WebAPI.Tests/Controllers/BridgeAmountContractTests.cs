using System.Text.Json;
using AZOA.WebAPI.Models.Responses;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Controllers;

public sealed class BridgeAmountContractTests
{
    [Fact]
    public void ResponseAmount_RoundTripsAsExactDecimalString()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var response = new BridgeTransactionResult { Amount = ulong.MaxValue };

        var json = JsonSerializer.Serialize(response, options);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("amount").GetString()
            .Should().Be("18446744073709551615");
        JsonSerializer.Deserialize<BridgeTransactionResult>(json, options)!
            .Amount.Should().Be(ulong.MaxValue);
    }

    [Fact]
    public void Response_DoesNotSerializeServerIdempotencyKey()
    {
        const string ledgerKey = "bridge-redeem:private-avatar:payment-intent";
        var response = new BridgeTransactionResult { IdempotencyKey = ledgerKey };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.Should().NotContain(ledgerKey)
            .And.NotContain("idempotencyKey");
        response.IdempotencyKey.Should().Be(ledgerKey);
    }
}
