using System.Net;
using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Dex;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed partial class TinymanProviderQuoteTests
{
    [Fact]
    public async Task LostSubmissionResponseReplaysExactConcatenatedWireWithoutSigningAgain()
    {
        using var fixture = new Fixture();
        var (intent, prepared) = await SignedSwapAsync(fixture);
        var sent = new List<byte[]>();
        fixture.Rpc.Submission = async (request, ct) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/v2/transactions");
            request.Content!.Headers.ContentType!.MediaType.Should().Be("application/x-binary");
            sent.Add(await request.Content.ReadAsByteArrayAsync(ct));
            if (sent.Count == 1) throw new HttpRequestException("Response lost after original bytes reached the node.");
            return Submitted(prepared.TransactionIds[0]);
        };
        var first = async () => await fixture.Provider.SubmitSwapStageAsync(intent, prepared);
        await first.Should().ThrowAsync<HttpRequestException>();
        var replay = await fixture.Provider.SubmitSwapStageAsync(intent, prepared);
        replay.IsError.Should().BeFalse(replay.Message);
        replay.Result.Should().Equal(prepared.TransactionIds);
        sent.Should().HaveCount(2);
        sent[1].Should().Equal(sent[0]);
        using var envelopes = JsonDocument.Parse(prepared.SignedPayloadJson);
        sent[0].Should().Equal(envelopes.RootElement.EnumerateArray().SelectMany(item => Convert.FromBase64String(item.GetString()!)));
        fixture.Custody.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("wire")]
    [InlineData("genesis")]
    [InlineData("expired")]
    public async Task ChangedOrExpiredSavedGroupNeverReachesSubmission(string change)
    {
        using var fixture = new Fixture();
        var (intent, prepared) = await SignedSwapAsync(fixture);
        if (change == "identity") prepared = prepared with { TransactionIds = ["different"] };
        if (change == "wire") prepared = prepared with { SignedPayloadJson = prepared.SignedPayloadJson + " " };
        if (change == "genesis") fixture.Rpc.Change = "genesis";
        if (change == "expired") fixture.Rpc.LastRound = 1901;
        var result = await fixture.Provider.SubmitSwapStageAsync(intent, prepared);
        result.IsError.Should().Be(change != "expired");
        fixture.Rpc.Posts.Should().Be(0);
        fixture.Custody.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DifferentRemoteTransactionIdentityRemainsAmbiguousRatherThanSuccess()
    {
        using var fixture = new Fixture();
        var (intent, prepared) = await SignedSwapAsync(fixture);
        fixture.Rpc.Submission = (_, _) => Task.FromResult(Submitted("different"));
        var send = async () => await fixture.Provider.SubmitSwapStageAsync(intent, prepared);
        await send.Should().ThrowAsync<HttpRequestException>();
        fixture.Rpc.Posts.Should().Be(1);
    }

    private static HttpResponseMessage Submitted(string id) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { txId = id }), System.Text.Encoding.UTF8, "application/json"),
    };

    private static async Task<(SwapIntent Intent, PreparedSwapStage Prepared)> SignedSwapAsync(Fixture fixture, bool optIn = false)
    {
        var requested = fixture.Terms();
        if (optIn)
        {
            fixture.Rpc.Change = "no-holding";
            requested = requested with { InputAsset = new("0", "ALGO", 6, true), OutputAsset = new(ulong.MaxValue.ToString(), "OUT", 6, false) };
        }
        var quote = (await fixture.Provider.QuoteSwapAsync(requested)).Result!;
        var intent = new SwapIntent(1, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            CanonicalJson.Hash(quote), quote, new string('a', 64));
        var planned = (await fixture.Provider.PlanSwapStageAsync(intent, optIn ? "OptIn" : "Swap", 0)).Result!;
        using var unsigned = JsonDocument.Parse(planned.UnsignedPayloadJson);
        var envelopes = unsigned.RootElement.GetProperty("transactions").EnumerateArray()
            .Select(value => Encoder.DecodeFromMsgPack<Transaction>(Convert.FromBase64String(value.GetString()!)))
            .Select(transaction => Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(transaction.Sign(RpcHandler.OwnerAccount))))
            .ToArray();
        return (intent, fixture.Provider.ValidateSignedSwapStage(intent, planned, JsonSerializer.Serialize(envelopes)).Result!);
    }
}
