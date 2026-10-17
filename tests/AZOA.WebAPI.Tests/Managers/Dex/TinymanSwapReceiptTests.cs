using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using Algorand.Algod.Model.Transactions;
using AZOA.WebAPI.Core.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class TinymanSwapReceiptTests
{
    [Fact]
    public void CleanupReceiptRequiresExactSelfCloseAndNeverInfersRentReleaseFromEffectAlone()
    {
        var wire = Wire(true) with { Stage = "CloseOptIn" };
        var block = Block(wire);
        TinymanSwapReceipt.TryRead(wire, Ids(wire), Element(block), out var observation).Should().BeTrue();
        observation!.ActualInput.Should().Be("0");
        observation.ActualOutput.Should().Be("0");
        observation.PlatformFeePaid.Should().Be("0");
        observation.LockedBalance.Should().Be("0");
        observation.NetworkFeePaid.Should().Be("1000");
        block["transactions"]![0]!["asset-transfer-transaction"]!["close-amount"] = 1UL;
        TinymanSwapReceipt.TryRead(wire, Ids(wire), Element(block), out _).Should().BeFalse();
        block["transactions"]![0]!["asset-transfer-transaction"]!["close-amount"] = 0UL;
        block["transactions"]![0]!["asset-transfer-transaction"]!["close-to"] = wire.Treasury;
        TinymanSwapReceipt.TryRead(wire, Ids(wire), Element(block), out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactFinalizedGroupProvesGrossInputOutputFeeAndNetworkCost(bool nativeInput)
    {
        var wire = Wire(nativeInput);
        var block = Block(wire);
        TinymanSwapReceipt.TryRead(wire, Ids(wire), Element(block), out var result).Should().BeTrue();
        result!.Verdict.Should().Be("Confirmed");
        result.ActualInput.Should().Be("10000"); result.ActualOutput.Should().Be("9500");
        result.PlatformFeePaid.Should().Be("100"); result.NetworkFeePaid.Should().Be("4000");
        result.LockedBalance.Should().Be("0"); result.EvidenceJson.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("genesis")]
    [InlineData("round")]
    [InlineData("id")]
    [InlineData("fee")]
    [InlineData("recipient")]
    [InlineData("inner-fee")]
    [InlineData("extra-inner")]
    [InlineData("logs")]
    [InlineData("order")]
    [InlineData("duplicate")]
    [InlineData("rekey")]
    public void ChangedSettlementEvidenceNeverConfirms(string change)
    {
        var wire = Wire(); var block = Block(wire); var txs = block["transactions"]!.AsArray();
        var inner = txs[1]!["inner-txns"]!.AsArray();
        switch (change)
        {
            case "genesis": block["genesis-hash"] = "another-network"; break;
            case "round": block["round"] = 1901UL; break;
            case "id": txs[0]!["id"] = "another-transaction"; break;
            case "fee": txs[0]!["fee"] = 999UL; break;
            case "recipient": inner[0]!["payment-transaction"]!["receiver"] = wire.Treasury; break;
            case "inner-fee": inner[0]!["fee"] = 1000UL; break;
            case "extra-inner": inner.Add(inner[0]!.DeepClone()); break;
            case "logs": txs[1]!["logs"]!.AsArray().RemoveAt(0); break;
            case "order": var first = txs[0]!.DeepClone(); txs[0] = txs[1]!.DeepClone(); txs[1] = first; break;
            case "duplicate": txs.Add(txs[0]!.DeepClone()); break;
            default: txs[0]!["rekey-to"] = wire.Treasury; break;
        }
        TinymanSwapReceipt.TryRead(wire, Ids(wire), Element(block), out _).Should().BeFalse();
    }

    [Fact]
    public void OptInEffectDoesNotInventIncrementalRent()
    {
        var wire = Wire(true) with { Stage = "OptIn" };
        TinymanSwapReceipt.TryRead(wire, Ids(wire), Element(Block(wire)), out var result).Should().BeTrue();
        result!.ActualInput.Should().Be("0"); result.PlatformFeePaid.Should().Be("0");
        result.NetworkFeePaid.Should().Be("1000"); result.LockedBalance.Should().BeNull();
    }

    [Theory]
    [InlineData(false, false, "100000")]
    [InlineData(true, false, "0")]
    [InlineData(false, true, "0")]
    public void OrderedPriorStateDistinguishesNewHoldingFromEarlierCompetingOptIn(bool heldBefore, bool competing, string expectedRent)
    {
        var wire = Wire(true) with { Stage = "OptIn" }; var block = Block(wire);
        if (competing)
        {
            var original = block["transactions"]![0]!;
            var prior = original.DeepClone(); prior["id"] = "earlier-self-opt-in"; prior["intra-round-offset"] = 0UL;
            original["intra-round-offset"] = 1UL;
            block["transactions"]!.AsArray().Insert(0, prior);
        }
        var account = Account(wire, heldBefore);
        TinymanOptInAccounting.TryResolve(wire, Ids(wire)[0], Element(block), Element(account), out var rent, out var proof).Should().BeTrue();
        rent.Should().Be(expectedRent); proof.Should().NotBeNullOrWhiteSpace();
        account["account"]!["round"] = 900UL;
        TinymanOptInAccounting.TryResolve(wire, Ids(wire)[0], Element(block), Element(account), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void EarlierCloseOutThenOriginalOptInCreatesNewRentAndLaterOptInDoesNotEraseIt()
    {
        var wire = Wire(true) with { Stage = "OptIn" }; var block = Block(wire); var txs = block["transactions"]!.AsArray();
        var prior = txs[0]!.DeepClone(); prior["id"] = "prior-close"; prior["intra-round-offset"] = 0UL;
        prior["asset-transfer-transaction"]!["close-to"] = wire.Treasury;
        txs[0]!["intra-round-offset"] = 1UL; txs.Insert(0, prior);
        var after = txs[1]!.DeepClone(); after["id"] = "later-opt-in"; after["intra-round-offset"] = 2UL; txs.Add(after);
        TinymanOptInAccounting.TryResolve(wire, Ids(wire)[0], Element(block), Element(Account(wire, true)), out var rent, out _).Should().BeTrue();
        rent.Should().Be("100000");
        txs[2]!["intra-round-offset"] = 1UL;
        TinymanOptInAccounting.TryResolve(wire, Ids(wire)[0], Element(block), Element(Account(wire, true)), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void PriorInnerOptInUsesExecutionTreeOrderRatherThanRepeatedIndexerOffset()
    {
        var wire = Wire(true) with { Stage = "OptIn" }; var block = Block(wire); var txs = block["transactions"]!.AsArray();
        var inner = txs[0]!.DeepClone(); inner["id"] = "prior-inner"; inner["intra-round-offset"] = 0UL;
        txs[0]!["intra-round-offset"] = 1UL;
        txs.Insert(0, new JsonObject { ["tx-type"] = "appl", ["id"] = "earlier-app", ["intra-round-offset"] = 0UL,
            ["inner-txns"] = new JsonArray(inner) });
        TinymanOptInAccounting.TryResolve(wire, Ids(wire)[0], Element(block), Element(Account(wire, false)), out var rent, out _).Should().BeTrue();
        rent.Should().Be("0");
    }

    internal static TinymanWireParameters Wire(bool nativeInput = false)
        => new("Swap", "receipt:fixture:Swap:0:original", new global::Algorand.Algod.Model.Account().Address.EncodeAsString(),
            new global::Algorand.Algod.Model.Account().Address.EncodeAsString(), 123,
            nativeInput ? 0 : ulong.MaxValue, nativeInput ? ulong.MaxValue : 0, 10000, 100, 9000, 900, 1900,
            Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()), "fixture-v1", 1000, 1000, 2000);

    internal static string[] Ids(TinymanWireParameters wire) => TinymanSwapWire.Build(wire).Select(tx => tx.TxID()).ToArray();
    internal static JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node);
    internal static JsonObject Account(TinymanWireParameters wire, bool present) => new()
    {
        ["account"] = new JsonObject { ["address"] = wire.Owner, ["round"] = 899UL, ["assets"] = present
            ? new JsonArray(new JsonObject { ["asset-id"] = wire.OutputAsset, ["amount"] = 0UL, ["is-frozen"] = false }) : new JsonArray() },
    };

    internal static JsonObject Block(TinymanWireParameters wire)
    {
        var txs = new JsonArray();
        var output = Math.Max(9500UL, wire.MinimumOutput);
        foreach (var original in TinymanSwapWire.Build(wire))
        {
            var tx = new JsonObject
            {
                ["id"] = original.TxID(), ["sender"] = wire.Owner, ["fee"] = original.Fee!.Value,
                ["first-valid"] = wire.FirstValid, ["last-valid"] = wire.LastValid, ["confirmed-round"] = 900UL,
                ["intra-round-offset"] = (ulong)txs.Count * 2, ["note"] = Convert.ToBase64String(original.Note),
            };
            if (original.Group is not null) tx["group"] = Convert.ToBase64String(original.Group.Bytes);
            if (original is ApplicationCallTransaction app)
            {
                tx["tx-type"] = "appl";
                tx["application-transaction"] = new JsonObject
                {
                    ["application-id"] = app.ApplicationId, ["on-completion"] = "noop",
                    ["application-args"] = new JsonArray(app.ApplicationArgs.Select(bytes => (JsonNode?)JsonValue.Create(Convert.ToBase64String(bytes))).ToArray()),
                    ["accounts"] = new JsonArray(app.Accounts.Select(address => (JsonNode?)JsonValue.Create(address.EncodeAsString())).ToArray()),
                    ["foreign-assets"] = new JsonArray(app.ForeignAssets.Select(asset => (JsonNode?)JsonValue.Create(asset)).ToArray()),
                    ["local-state-schema"] = new JsonObject { ["num-byte-slice"] = 0UL, ["num-uint"] = 0UL },
                    ["global-state-schema"] = new JsonObject { ["num-byte-slice"] = 0UL, ["num-uint"] = 0UL },
                    ["foreign-apps"] = new JsonArray(), ["box-references"] = new JsonArray(), ["access"] = new JsonArray(),
                };
                var inner = Transfer(wire.OutputAsset, wire.Owner, output);
                inner["sender"] = TinymanV2PoolLocator.GetPoolAddress(wire.Validator, wire.InputAsset, wire.OutputAsset);
                inner["fee"] = 0UL; inner["intra-round-offset"] = 2UL;
                tx["inner-txns"] = new JsonArray(inner);
                string[] names = ["input_asset_id", "input_amount", "swap_amount", "change", "output_asset_id", "output_amount", "poolers_fee_amount", "protocol_fee_amount", "total_fee_amount"];
                ulong[] values = [wire.InputAsset, wire.GrossInput - wire.PlatformFee, wire.GrossInput - wire.PlatformFee - 30, 0, wire.OutputAsset, output, 25, 5, 30];
                var logs = new JsonArray();
                for (var index = 0; index < names.Length; index++)
                {
                    var prefix = System.Text.Encoding.UTF8.GetBytes(names[index] + " %i");
                    var bytes = new byte[prefix.Length + 8]; prefix.CopyTo(bytes, 0);
                    BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(prefix.Length), values[index]); logs.Add(Convert.ToBase64String(bytes));
                }
                tx["logs"] = logs;
            }
            else
            {
                var transfer = original is PaymentTransaction payment ? Transfer(0, payment.Receiver.EncodeAsString(), (ulong)payment.Amount!)
                    : Transfer((ulong)((AssetTransferTransaction)original).XferAsset!, ((AssetTransferTransaction)original).AssetReceiver.EncodeAsString(), (ulong)((AssetTransferTransaction)original).AssetAmount!);
                foreach (var property in transfer) tx[property.Key] = property.Value!.DeepClone();
                if (original is AssetTransferTransaction { AssetCloseTo: not null } close)
                {
                    tx["asset-transfer-transaction"]!["close-to"] = close.AssetCloseTo.EncodeAsString();
                    tx["asset-transfer-transaction"]!["close-amount"] = 0UL;
                }
            }
            txs.Add(tx);
        }
        return new() { ["round"] = 900UL, ["genesis-hash"] = wire.GenesisHash, ["genesis-id"] = wire.GenesisId, ["transactions"] = txs };
    }

    private static JsonObject Transfer(ulong asset, string receiver, ulong amount)
    {
        var body = new JsonObject { ["receiver"] = receiver, ["amount"] = amount };
        if (asset != 0) body["asset-id"] = asset;
        return new() { ["tx-type"] = asset == 0 ? "pay" : "axfer", [asset == 0 ? "payment-transaction" : "asset-transfer-transaction"] = body };
    }
}
