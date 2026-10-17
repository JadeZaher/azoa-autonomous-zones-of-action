using System.Text.Json;
using System.Text.Json.Nodes;
using AZOA.WebAPI.Core.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class TinymanPoolStateTests
{
    private const ulong Validator = 123;
    private const ulong Asset = ulong.MaxValue;
    private static readonly string ValidatorAddress = global::Algorand.Address.ForApplication(Validator).ToString();

    [Fact]
    public void FullWidthPairCurrentFeesAndDirectionArePreserved()
    {
        var account = Account();
        TinymanPoolState.TryRead(Element(account), Validator, Asset, 0, out var pool).Should().BeTrue();
        pool!.Asset1.Should().Be(ulong.MaxValue);
        pool.Asset2.Should().Be(0);
        pool.TotalFeeShare.Should().Be(42);
        pool.TryQuote(Asset, 10000, out var first).Should().BeTrue();
        pool.TryQuote(0, 10000, out var reverse).Should().BeTrue();
        first!.TotalFee.Should().Be(42);
        first.Output.Should().NotBe(reverse!.Output);
        pool.TryQuote(7, 10000, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("address")]
    [InlineData("authority")]
    [InlineData("application")]
    [InlineData("pair")]
    [InlineData("duplicate-app")]
    [InlineData("duplicate-key")]
    [InlineData("missing-reserve")]
    [InlineData("wrong-type")]
    [InlineData("locked")]
    [InlineData("fee")]
    [InlineData("ratio")]
    [InlineData("round")]
    public void WrongAuthorityOrMalformedPoolEvidenceCannotQuote(string change)
    {
        var account = Account();
        var applications = account["apps-local-state"]!.AsArray();
        var app = applications[0]!.AsObject();
        var values = app["key-value"]!.AsArray();
        switch (change)
        {
            case "address": account["address"] = "other-pool"; break;
            case "authority": account["auth-addr"] = "other-validator"; break;
            case "application": app["id"] = 999; break;
            case "pair": Value(values, "asset_1_id")["uint"] = 7; break;
            case "duplicate-app": applications.Add(app.DeepClone()); break;
            case "duplicate-key": values.Add(values[0]!.DeepClone()); break;
            case "missing-reserve": values.RemoveAt(2); break;
            case "wrong-type": Value(values, "asset_1_reserves")["type"] = 1; break;
            case "locked": Value(values, "lock")["uint"] = 1; break;
            case "fee": Value(values, "total_fee_share")["uint"] = 0; break;
            case "ratio": Value(values, "protocol_fee_ratio")["uint"] = 11; break;
            case "round": account["round"] = 0; break;
        }
        TinymanPoolState.TryRead(Element(account), Validator, Asset, 0, out _).Should().BeFalse();
    }

    [Fact]
    public void AccumulatedProtocolFeeOverflowRefusesOtherwiseValidReserveQuote()
    {
        var account = Account();
        Value(account["apps-local-state"]![0]!["key-value"]!.AsArray(), "asset_1_protocol_fees")["uint"] = ulong.MaxValue;
        TinymanPoolState.TryRead(Element(account), Validator, Asset, 0, out var pool).Should().BeTrue();
        pool!.TryQuote(Asset, 10000, out var quote).Should().BeFalse();
        quote.Should().BeNull();
    }

    internal static JsonObject Account()
    {
        var values = new Dictionary<string, ulong>
        {
            ["asset_1_id"] = Asset, ["asset_2_id"] = 0, ["asset_1_reserves"] = 1000000,
            ["asset_2_reserves"] = 2000000, ["asset_1_protocol_fees"] = 10, ["asset_2_protocol_fees"] = 20,
            ["total_fee_share"] = 42, ["protocol_fee_ratio"] = 6, ["lock"] = 0,
        };
        return new JsonObject
        {
            ["address"] = TinymanV2PoolLocator.GetPoolAddress(Validator, Asset, 0),
            ["auth-addr"] = ValidatorAddress, ["round"] = 900,
            ["apps-local-state"] = new JsonArray(new JsonObject
            {
                ["id"] = Validator,
                ["key-value"] = new JsonArray(values.Select(pair => (JsonNode)new JsonObject
                {
                    ["key"] = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(pair.Key)),
                    ["value"] = new JsonObject { ["type"] = 2, ["uint"] = pair.Value },
                }).ToArray()),
            }),
        };
    }

    private static JsonObject Value(JsonArray values, string key)
        => values.Single(node => node!["key"]!.GetValue<string>() ==
            Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(key)))!["value"]!.AsObject();

    private static JsonElement Element(JsonObject value) => JsonSerializer.SerializeToElement(value);
}
