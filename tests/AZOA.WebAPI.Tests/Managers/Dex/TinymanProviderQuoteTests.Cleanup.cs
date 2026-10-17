using System.Text.Json;
using System.Text.Json.Nodes;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using FluentAssertions;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed partial class TinymanProviderQuoteTests
{
    [Theory]
    [InlineData("valid", false)]
    [InlineData("positive-balance", true)]
    [InlineData("creator", true)]
    [InlineData("preexisting-holding", true)]
    [InlineData("no-replacement-consent", true)]
    [InlineData("unknown-purpose", true)]
    public async Task CleanupNeedsNewConsentOriginalAttributionAndCurrentZeroHolding(string change, bool refused)
    {
        using var fixture = new Fixture();
        var (intent, optIn) = await SignedSwapAsync(fixture, true);
        var wire = JsonSerializer.Deserialize<TinymanWireParameters>(optIn.Plan.RecoveryMetadata)!;
        var block = TinymanSwapReceiptTests.Block(wire);
        using var blockDocument = JsonDocument.Parse(block.ToJsonString());
        TinymanSwapReceipt.TryRead(wire, optIn.TransactionIds, blockDocument.RootElement, out var observed).Should().BeTrue();
        fixture.Rpc.Change = "";
        fixture.Rpc.HoldingAmount = change == "positive-balance" ? 1UL : 0UL;
        if (change == "creator") fixture.Rpc.AssetCreator = RpcHandler.Owner;
        fixture.Rpc.Indexed = (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            JsonNode result = path == "/v2/transactions"
                ? new JsonObject { ["current-round"] = 900UL, ["transactions"] = new JsonArray(block["transactions"]![0]!.DeepClone()) }
                : path.StartsWith("/v2/accounts/", StringComparison.Ordinal)
                    ? TinymanSwapReceiptTests.Account(wire, change == "preexisting-holding") : block;
            return Task.FromResult(JsonResponse(result));
        };
        var consent = new SwapCleanupConsent(1, intent.OperationId, intent.QuoteHash, new string('a', 64), intent.Terms.ActorUserId,
            null, intent.Terms.Owner, intent.Terms.OutputAsset.Id, "CloseCurrentZeroHolding", "1000", change != "no-replacement-consent");
        IReadOnlyList<SwapStageHistory> history = [new(optIn, observed!)];
        if (change == "unknown-purpose") history = [new(optIn, observed!), new(optIn with { Plan = optIn.Plan with { Purpose = "Unknown" } }, observed!)];
        var result = await fixture.Provider.PlanSwapCleanupAsync(intent, history, consent, "CloseOptIn", 0);
        result.IsError.Should().Be(refused, result.Message);
        if (!refused)
        {
            result.Result!.CleanupConsent.Should().Be(consent);
            result.Result.Purpose.Should().Be("Cleanup");
            result.Result.ReservedNetworkFee.Should().Be("1000");
            using var unsigned = JsonDocument.Parse(result.Result.UnsignedPayloadJson);
            var transaction = Convert.FromBase64String(unsigned.RootElement.GetProperty("transactions")[0].GetString()!);
            AlgorandExactWire.HasAddressField(transaction, "aclose", RpcHandler.Owner).Should().BeTrue("the close-out returns to the owner");
            AlgorandExactWire.HasField(transaction, "aamt").Should().BeFalse("a close-out moves no explicit amount");
            AlgorandExactWire.HasAddressField(transaction, "arcv", RpcHandler.Owner).Should().BeTrue();
        }
        fixture.Rpc.Posts.Should().Be(0);
        fixture.Custody.VerifyNoOtherCalls();
    }
}
