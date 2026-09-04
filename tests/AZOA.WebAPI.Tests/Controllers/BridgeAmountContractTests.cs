using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
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
            .Amount.Should().Be(RailAmount.OfChainAsset(ulong.MaxValue));
    }

    /// <summary>
    /// D13: the wire contract is a decimal base-unit string and the domain type is now a
    /// <see cref="RailAmount"/>, so the response can carry an amount the old <c>ulong</c>
    /// property could not express at all — 1000 ETH in wei is ~54× over the 64-bit line.
    /// </summary>
    [Theory]
    [InlineData("18446744073709551616")]
    [InlineData("1000000000000000000000")]
    [InlineData("115792089237316195423570985008687907853269984665640564039457584007913129639935")]
    public void ResponseAmount_AboveUlongMax_RoundTripsWithoutTruncation(string baseUnits)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var response = new BridgeTransactionResult
        {
            Amount = RailAmount.OfChainAsset(BigInteger.Parse(baseUnits)),
        };

        var json = JsonSerializer.Serialize(response, options);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("amount").GetString().Should().Be(baseUnits);
        JsonSerializer.Deserialize<BridgeTransactionResult>(json, options)!
            .Amount.BaseUnits.Should().Be(BigInteger.Parse(baseUnits));
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
