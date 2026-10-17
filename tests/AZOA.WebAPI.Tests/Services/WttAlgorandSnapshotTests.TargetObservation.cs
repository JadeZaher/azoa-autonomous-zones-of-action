using System.Text.Json;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed partial class WttAlgorandSnapshotTests
{
    [Theory]
    [InlineData("exact", true)]
    [InlineData("code", false)]
    [InlineData("inner", false)]
    [InlineData("group", false)]
    [InlineData("pagination", false)]
    public async Task Target_observation_uses_original_wire_and_complete_executed_program_evidence(string mutation, bool confirmed)
    {
        using var fixture = new AlgoPreparationFixture(target: true);
        var (intent, proofs, quote) = await fixture.TargetIntentAsync();
        var preparation = await fixture.Provider.PrepareWttTargetAsync(intent, proofs, quote, "TransferSequenceStorage", 0);
        Assert.False(preparation.IsError, preparation.Message);
        var prepared = preparation.Result!;
        var signed = new[] { prepared.SignedTransactionBase64 }
            .Concat(JsonSerializer.Deserialize<string[]>(prepared.AdditionalSignedTransactionsJson!)!);
        var expected = signed.Select(value => AlgorandSignedEnvelope.DecodeCanonical(value).Tx).ToArray();
        var transactions = expected.Select((value, index) => AlgorandGroupReceiptTests.Indexed(value, 500, (ulong)index)).ToArray();
        if (mutation == "group") transactions[0]["fee"] = 1UL;
        if (mutation == "inner") transactions[^1]["inner-txns"] = new[] { new { arbitrary = true } };
        var block = new Dictionary<string, object> { ["round"] = 500UL, ["timestamp"] = 1799999990UL,
            ["genesis-hash"] = Rpc.Genesis, ["genesis-id"] = "localnet-v1", ["transactions"] = transactions };
        var creation = new Dictionary<string, object> { ["id"] = "token-creation", ["confirmed-round"] = 1UL,
            ["intra-round-offset"] = 0UL, ["created-application-index"] = 9UL,
            ["application-transaction"] = new Dictionary<string, object> { ["application-id"] = 0UL,
                ["on-completion"] = "noop", ["approval-program"] = mutation == "code" ? "BAUG" : "AQID", ["clear-state-program"] = "AQID" } };
        fixture.Rpc.ReadOverride = request =>
        {
            if (request.RequestUri!.Host != "indexer.test") return null;
            if (request.RequestUri.AbsolutePath == "/v2/blocks/500") return Json(block);
            if (request.RequestUri.Query.Contains("txid=", StringComparison.Ordinal))
                return Json(new Dictionary<string, object> { ["current-round"] = 500UL, ["transactions"] = new[] { transactions[0] } });
            Assert.Contains("application-id=9", request.RequestUri.Query);
            var page = new Dictionary<string, object> { ["current-round"] = 500UL, ["transactions"] = new[] { creation, transactions[1] } };
            if (mutation == "pagination") page["next-token"] = new { invalid = true };
            return Json(page);
        };
        fixture.AllowCustody = false;
        var observed = await fixture.Provider.ObserveWttTargetAsync(intent, proofs, quote, prepared);
        Assert.Equal(confirmed, !observed.IsError && observed.Result?.Verdict == "Confirmed");
        if (confirmed)
        {
            Assert.Equal(prepared.Reservation.NetworkFee, observed.Result!.NetworkFeePaid);
            Assert.Equal(observed.Result, (await fixture.Provider.ObserveWttTargetAsync(intent, proofs, quote, prepared)).Result);
        }
        Assert.Equal(1, fixture.CustodyCalls);
    }
}
