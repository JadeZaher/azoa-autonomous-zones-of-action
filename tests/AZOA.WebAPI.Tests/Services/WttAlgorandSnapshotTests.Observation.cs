using System.Net;
using System.Text.Json;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed partial class WttAlgorandSnapshotTests
{
    [Theory]
    [InlineData("exact", true)]
    [InlineData("changed-code", false)]
    [InlineData("history-cycle", false)]
    [InlineData("partial-token", false)]
    [InlineData("changed-group", false)]
    public async Task Source_observer_resumes_fixed_boundary_history_and_requires_exact_group_and_executed_code(string mutation, bool confirms)
    {
        using var fixture = new AlgoPreparationFixture();
        var (intent, quote) = await fixture.IntentAsync();
        var preparation = await fixture.Provider.PrepareWttSourceAsync(intent, quote, "EmitterStorage", 0);
        Assert.False(preparation.IsError, preparation.Message);
        var prepared = preparation.Result!;
        var envelopes = new[] { prepared.SignedTransactionBase64 }
            .Concat(JsonSerializer.Deserialize<string[]>(prepared.AdditionalSignedTransactionsJson!)!).ToArray();
        var expected = envelopes.Select(value => AlgorandSignedEnvelope.DecodeCanonical(value).Tx).ToArray();
        var transactions = expected.Select((tx, index) => AlgorandGroupReceiptTests.Indexed(tx, 500, (ulong)index)).ToArray();
        if (mutation == "changed-group") transactions[0]["fee"] = 1UL;
        var block = new Dictionary<string, object> { ["round"] = 500UL, ["timestamp"] = 1799999990UL,
            ["genesis-hash"] = Rpc.Genesis, ["genesis-id"] = "localnet-v1", ["transactions"] = transactions };
        var creation = new Dictionary<string, object> { ["id"] = "core-creation", ["confirmed-round"] = 1UL,
            ["intra-round-offset"] = 0UL, ["created-application-index"] = 7UL,
            ["application-transaction"] = new Dictionary<string, object> { ["application-id"] = 0UL, ["on-completion"] = "noop",
                ["approval-program"] = mutation == "changed-code" ? "BAUG" : "AQID", ["clear-state-program"] = "AQID" } };
        var pages = 0;
        fixture.Rpc.ReadOverride = request =>
        {
            if (request.RequestUri!.Host != "indexer.test") return null;
            if (request.RequestUri.AbsolutePath == "/v2/blocks/500") return Json(block);
            var query = request.RequestUri.Query;
            if (query.Contains("txid=", StringComparison.Ordinal))
                return Json(new Dictionary<string, object> { ["current-round"] = 500UL, ["transactions"] = new[] { transactions[0] } });
            Assert.Contains("application-id=7", query); Assert.Contains("max-round=500", query); Assert.Contains("limit=25", query);
            pages++;
            if (!query.Contains("next=", StringComparison.Ordinal))
                return Json(new Dictionary<string, object> { ["current-round"] = 500UL, ["transactions"] = new[] { creation },
                    ["next-token"] = mutation == "partial-token" ? 123 : "page-2" });
            Assert.Contains("next=page-2", query);
            var result = new Dictionary<string, object> { ["current-round"] = 500UL, ["transactions"] = new[] { transactions[1] } };
            if (mutation == "history-cycle") result["next-token"] = "page-2";
            return Json(result);
        };
        fixture.AllowCustody = false;
        var first = await fixture.Provider.ObserveWttSourceAsync(intent, quote, prepared);
        Assert.False(first.IsError, first.Message);
        Assert.Equal("Pending", first.Result!.Verdict);
        var second = await fixture.Provider.ObserveWttSourceAsync(intent, quote, prepared);
        Assert.Equal(confirms, !second.IsError && second.Result?.Verdict == "Confirmed");
        if (confirms)
        {
            Assert.Equal(2, pages);
            Assert.Equal(prepared.Reservation.NetworkFee, second.Result!.NetworkFeePaid);
            Assert.True(fixture.Evidence.Values.Single().Complete);
            var again = await fixture.Provider.ObserveWttSourceAsync(intent, quote, prepared);
            Assert.Equal(second.Result, again.Result);
            Assert.Equal(2, pages);
        }
        Assert.Equal(1, fixture.CustodyCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expiry_requires_caught_up_exhausted_queries_for_every_group_member(bool truncated)
    {
        using var fixture = new AlgoPreparationFixture();
        var (intent, quote) = await fixture.IntentAsync();
        var preparation = await fixture.Provider.PrepareWttSourceAsync(intent, quote, "EmitterStorage", 0);
        Assert.False(preparation.IsError, preparation.Message);
        var queries = 0;
        fixture.Rpc.ReadOverride = request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/v2/transactions/params") return Json(new Dictionary<string, object> {
                ["last-round"] = 1501UL, ["genesis-hash"] = Rpc.Genesis, ["genesis-id"] = "localnet-v1" });
            if (uri.Host == "indexer.test" && uri.AbsolutePath == "/v2/transactions")
            {
                queries++;
                var page = new Dictionary<string, object> { ["current-round"] = 1501UL, ["transactions"] = Array.Empty<object>() };
                if (truncated && queries == 2) page["next-token"] = true;
                return Json(page);
            }
            if (uri.AbsolutePath == "/v2/blocks/1500/hash") return Json(new { blockHash = new string('A', 52) });
            if (uri.AbsolutePath == "/v2/blocks/1500") return uri.Host == "indexer.test"
                ? Json(new Dictionary<string, object> { ["round"] = 1500UL, ["timestamp"] = 1800000000UL, ["genesis-hash"] = Rpc.Genesis })
                : Json(new { block = new { rnd = 1500, ts = 1800000000, gh = Rpc.Genesis } });
            return null;
        };
        fixture.AllowCustody = false;
        var observed = await fixture.Provider.ObserveWttSourceAsync(intent, quote, preparation.Result!);
        Assert.False(observed.IsError, observed.Message);
        Assert.Equal(truncated ? "Pending" : "ExpiredNoEffect", observed.Result!.Verdict);
        Assert.Equal(2, queries);
        Assert.Equal(1, fixture.CustodyCalls);
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") };
}
