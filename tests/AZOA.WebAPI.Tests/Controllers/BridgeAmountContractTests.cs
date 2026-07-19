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
}
