using System.Text.Json;
using System.Text.Json.Nodes;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed partial class TinymanProviderQuoteTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RentReleaseResumesSavedPageAndAttributesEarlierIncarnationCloseSeparately(bool externalClose, bool cursorCycle)
    {
        using var fixture = new Fixture();
        var (intent, allocation) = await SignedSwapAsync(fixture, true);
        var originalWire = JsonSerializer.Deserialize<TinymanWireParameters>(allocation.Plan.RecoveryMetadata)!;
        var block = TinymanSwapReceiptTests.Block(originalWire);
        fixture.Rpc.Change = ""; fixture.Rpc.HoldingAmount = 0;
        fixture.Rpc.Indexed = (request, _) => Task.FromResult(JsonResponse(request.RequestUri!.AbsolutePath == "/v2/transactions"
            ? new JsonObject { ["current-round"] = 900UL, ["transactions"] = new JsonArray(block["transactions"]![0]!.DeepClone()) }
            : request.RequestUri.AbsolutePath.StartsWith("/v2/accounts/", StringComparison.Ordinal)
                ? TinymanSwapReceiptTests.Account(originalWire, false) : block));
        TinymanSwapReceipt.TryRead(originalWire, allocation.TransactionIds, TinymanSwapReceiptTests.Element(block), out var observed).Should().BeTrue();
        var consent = new SwapCleanupConsent(1, intent.OperationId, intent.QuoteHash, new string('a', 64), intent.Terms.ActorUserId,
            null, intent.Terms.Owner, intent.Terms.OutputAsset.Id, "CloseCurrentZeroHolding", "1000", true);
        var plan = (await fixture.Provider.PlanSwapCleanupAsync(intent, [new(allocation, observed!)], consent, "CloseOptIn", 0)).Result!;
        using var unsigned = JsonDocument.Parse(plan.UnsignedPayloadJson);
        var envelopes = unsigned.RootElement.GetProperty("transactions").EnumerateArray()
            .Select(value => Encoder.DecodeFromMsgPack<Transaction>(Convert.FromBase64String(value.GetString()!)))
            .Select(transaction => Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(transaction.Sign(RpcHandler.OwnerAccount)))).ToArray();
        var cleanup = fixture.Provider.ValidateSignedSwapStage(intent, plan, JsonSerializer.Serialize(envelopes)).Result!;
        var cleanupWire = JsonSerializer.Deserialize<TinymanWireParameters>(cleanup.Plan.RecoveryMetadata)!;
        var closingTx = TinymanSwapReceiptTests.Block(cleanupWire)["transactions"]![0]!.DeepClone();
        closingTx["intra-round-offset"] = 4UL;
        if (externalClose)
        {
            var prior = closingTx.DeepClone(); prior["id"] = "external-close"; prior["intra-round-offset"] = 1UL;
            block["transactions"]!.AsArray().Add(prior);
            var reopen = block["transactions"]![0]!.DeepClone(); reopen["id"] = "external-reopen"; reopen["intra-round-offset"] = 2UL;
            block["transactions"]!.AsArray().Add(reopen);
        }
        block["transactions"]!.AsArray().Add(closingTx);
        var pages = 0;
        fixture.Rpc.Indexed = (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/v2/accounts/", StringComparison.Ordinal)) return Task.FromResult(JsonResponse(TinymanSwapReceiptTests.Account(originalWire, false)));
            if (path.StartsWith("/v2/blocks/", StringComparison.Ordinal)) return Task.FromResult(JsonResponse(block));
            if (request.RequestUri.Query.Contains("txid=", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(new JsonObject { ["current-round"] = 900UL, ["transactions"] = new JsonArray(
                    (request.RequestUri.Query.Contains(cleanup.TransactionIds[0], StringComparison.Ordinal) ? closingTx : block["transactions"]![0]!).DeepClone()) }));
            pages++;
            var next = request.RequestUri.Query.Contains("next=page-two", StringComparison.Ordinal);
            var result = new JsonObject { ["current-round"] = 900UL, ["transactions"] = next ? new JsonArray() : new JsonArray(closingTx.DeepClone()) };
            if (!next) result["next-token"] = "page-two";
            else if (cursorCycle) result["next-token"] = "page-three";
            return Task.FromResult(JsonResponse(result));
        };
        var first = (await fixture.Provider.ScanSwapRentReleaseAsync(intent, allocation, cleanup, null)).Result!;
        first.Complete.Should().BeFalse(); first.ReleasedLockedBalance.Should().BeNull(); first.EvidenceJson.Should().BeNull();
        var saved = first.CheckpointJson;
        var final = (await fixture.Provider.ScanSwapRentReleaseAsync(intent, allocation, cleanup, saved)).Result!;
        if (cursorCycle)
        {
            final.Complete.Should().BeFalse(); final.ReleasedLockedBalance.Should().BeNull();
            var refused = await fixture.Provider.ScanSwapRentReleaseAsync(intent, allocation, cleanup, final.CheckpointJson);
            refused.IsError.Should().BeTrue(); refused.Message.Should().Contain("cursor did not advance");
            refused.Result.Should().BeNull(); fixture.Rpc.Posts.Should().Be(0); return;
        }
        final.Complete.Should().BeTrue(); final.ReleasedLockedBalance.Should().Be("100000");
        using var proof = JsonDocument.Parse(final.EvidenceJson!);
        proof.RootElement.GetProperty("releasedBy").GetString().Should().Be(externalClose ? "EarlierExternalClose" : "Cleanup");
        var repeated = (await fixture.Provider.ScanSwapRentReleaseAsync(intent, allocation, cleanup, saved)).Result!;
        repeated.Should().Be(final);
        pages.Should().Be(3); fixture.Rpc.Posts.Should().Be(0); fixture.Custody.VerifyNoOtherCalls();
    }
}
