using System.Text.Json;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed partial class WttAlgorandSnapshotTests
{
    [Theory]
    [InlineData("exact", false)]
    [InlineData("amount", true)]
    [InlineData("asset", true)]
    [InlineData("recipient", true)]
    [InlineData("sender", true)]
    [InlineData("fee", true)]
    [InlineData("close", true)]
    [InlineData("rekey", true)]
    [InlineData("extra", true)]
    [InlineData("nested", true)]
    public async Task Target_inner_receipt_proves_exact_actual_asset_delivery(string mutation, bool rejected)
    {
        using var fixture = new AlgoPreparationFixture(target: true);
        var (intent, _, quote) = await fixture.TargetIntentAsync();
        var plan = JsonSerializer.Deserialize<WttAlgorandTargetPlan>(quote.PlanJson)!;
        var transfer = new Dictionary<string, object>
        {
            ["asset-id"] = mutation == "asset" ? 9UL : ulong.MaxValue,
            ["amount"] = mutation == "amount" ? 1UL : 100000000UL,
            ["receiver"] = mutation == "recipient" ? Rpc.Recipient : intent.RecipientAddress,
        };
        if (mutation == "close") transfer["close-to"] = intent.TargetAuthority.Address;
        var inner = new Dictionary<string, object>
        {
            ["tx-type"] = "axfer", ["sender"] = mutation == "sender" ? intent.TargetAuthority.Address : plan.CustodyAddress,
            ["fee"] = mutation == "fee" ? 1UL : 0UL, ["asset-transfer-transaction"] = transfer,
        };
        if (mutation == "rekey") inner["rekey-to"] = intent.TargetAuthority.Address;
        if (mutation == "nested") inner["inner-txns"] = new[] { new { unexpected = true } };
        var children = mutation == "extra" ? new[] { inner, inner } : new[] { inner };
        var root = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["inner-txns"] = children });
        if (rejected) Assert.Throws<InvalidDataException>(() => WttAlgorandTargetEffects.Read(intent, plan, "Redeem", ulong.MaxValue, [root]));
        else Assert.Equal(ulong.MaxValue, WttAlgorandTargetEffects.Read(intent, plan, "Redeem", ulong.MaxValue, [root]));
    }
}
