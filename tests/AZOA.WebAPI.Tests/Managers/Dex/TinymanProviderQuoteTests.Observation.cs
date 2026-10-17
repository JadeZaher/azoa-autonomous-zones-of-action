using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed partial class TinymanProviderQuoteTests
{
    [Fact]
    public async Task FullFinalizedBlockConfirmsActualSettlementWithoutNewCustodyOrSubmission()
    {
        using var fixture = new Fixture();
        var (intent, prepared) = await SignedSwapAsync(fixture);
        var wire = JsonSerializer.Deserialize<TinymanWireParameters>(prepared.Plan.RecoveryMetadata)!;
        var block = TinymanSwapReceiptTests.Block(wire);
        fixture.Rpc.Indexed = (request, _) => Task.FromResult(JsonResponse(request.RequestUri!.AbsolutePath == "/v2/transactions"
            ? new JsonObject { ["current-round"] = 900UL, ["transactions"] = new JsonArray(block["transactions"]![0]!.DeepClone()) } : block));
        var result = await fixture.Provider.ObserveSwapStageAsync(intent, prepared);
        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Verdict.Should().Be("Confirmed"); result.Result.ActualOutput.Should().Be(Math.Max(9500UL, wire.MinimumOutput).ToString());
        result.Result.NetworkFeePaid.Should().Be("3000"); result.Result.PlatformFeePaid.Should().Be("0");
        fixture.Rpc.Posts.Should().Be(0); fixture.Custody.VerifyNoOtherCalls();
        fixture.Rpc.Change = "code";
        (await fixture.Provider.ObserveSwapStageAsync(intent, prepared)).Result!.Verdict.Should().Be("Pending");
    }

    [Theory]
    [InlineData("complete", "ExpiredAbsent")]
    [InlineData("lagging", "Pending")]
    [InlineData("pagination", "Pending")]
    [InlineData("wrong-genesis", "Pending")]
    [InlineData("one-member-found", "Pending")]
    public async Task ExpiryRequiresCompleteCaughtUpAbsenceForEveryGroupMember(string mode, string verdict)
    {
        using var fixture = new Fixture();
        var (intent, prepared) = await SignedSwapAsync(fixture);
        fixture.Rpc.LastRound = 1901;
        fixture.Rpc.Indexed = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/v2/blocks/", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(new JsonObject { ["round"] = 1900UL,
                    ["genesis-hash"] = mode == "wrong-genesis" ? "different" : RpcHandler.Genesis }));
            var response = new JsonObject { ["current-round"] = mode == "lagging" ? 1899UL : 1900UL, ["transactions"] = new JsonArray() };
            if (mode == "pagination") response["next-token"] = "not-exhausted";
            if (mode == "one-member-found" && request.RequestUri.Query.Contains(prepared.TransactionIds[1], StringComparison.Ordinal))
                response["transactions"] = new JsonArray(new JsonObject { ["id"] = prepared.TransactionIds[1] });
            return Task.FromResult(JsonResponse(response));
        };
        var result = await fixture.Provider.ObserveSwapStageAsync(intent, prepared);
        result.Result!.Verdict.Should().Be(verdict);
        if (verdict == "ExpiredAbsent") result.Result.NetworkFeePaid.Should().Be("0");
        else result.Result.NetworkFeePaid.Should().BeNull();
        fixture.Rpc.Posts.Should().Be(0);
    }

    [Fact]
    public async Task ConfirmedOptInRetainsUnknownRentUntilHistoricalEndpointAndOrderedBlockResolveIt()
    {
        using var fixture = new Fixture();
        var (intent, prepared) = await SignedSwapAsync(fixture, optIn: true);
        var wire = JsonSerializer.Deserialize<TinymanWireParameters>(prepared.Plan.RecoveryMetadata)!;
        var block = TinymanSwapReceiptTests.Block(wire);
        var target = block["transactions"]![0]!;
        var prior = target.DeepClone(); prior["id"] = "external-earlier-opt-in";
        target["intra-round-offset"] = 1UL; block["transactions"]!.AsArray().Insert(0, prior);
        var historyEnabled = false;
        fixture.Rpc.Indexed = (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/v2/accounts/", StringComparison.Ordinal))
            {
                request.RequestUri.Query.Should().Contain("round=899").And.Contain("include-all=true");
                return Task.FromResult(historyEnabled ? JsonResponse(TinymanSwapReceiptTests.Account(wire, false))
                    : new HttpResponseMessage(HttpStatusCode.BadRequest));
            }
            return Task.FromResult(JsonResponse(path == "/v2/transactions"
                ? new JsonObject { ["current-round"] = 900UL, ["transactions"] = new JsonArray(target.DeepClone()) } : block));
        };
        var confirmed = (await fixture.Provider.ObserveSwapStageAsync(intent, prepared)).Result!;
        confirmed.Verdict.Should().Be("Confirmed"); confirmed.LockedBalance.Should().BeNull();
        (await fixture.Provider.ResolveSwapStageAccountingAsync(intent, prepared)).Result.Should().BeNull();
        historyEnabled = true;
        var resolved = (await fixture.Provider.ResolveSwapStageAccountingAsync(intent, prepared)).Result!;
        resolved.ActualLockedBalance.Should().Be("0"); resolved.EvidenceJson.Should().Contain("presentBefore");
        (await fixture.Provider.ObserveSwapStageAsync(intent, prepared)).Result!.LockedBalance.Should().BeNull();
        fixture.Rpc.Posts.Should().Be(0); fixture.Custody.VerifyNoOtherCalls();
    }

    private static HttpResponseMessage JsonResponse(JsonNode value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
    };
}
