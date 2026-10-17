using System.Text.Json;
using System.Text.Json.Nodes;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed partial class TinymanProviderQuoteTests
{
    [Fact]
    public async Task ExposedUnsignedPlanRecoversOnlyActualOriginalSignaturesFromFinalizedGroup()
    {
        using var fixture = new Fixture();
        var (intent, signed) = await SignedSwapAsync(fixture);
        var wire = JsonSerializer.Deserialize<TinymanWireParameters>(signed.Plan.RecoveryMetadata)!;
        var block = TinymanSwapReceiptTests.Block(wire);
        var envelopes = JsonSerializer.Deserialize<string[]>(signed.SignedPayloadJson)!;
        for (var index = 0; index < envelopes.Length; index++)
            block["transactions"]![index]!["signature"] = new JsonObject
            {
                ["sig"] = Convert.ToBase64String(AlgorandSignedEnvelope.DecodeCanonical(envelopes[index]).Sig.Bytes),
            };
        var corrupt = false;
        fixture.Rpc.Indexed = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath != "/v2/transactions") return Task.FromResult(JsonResponse(block));
            var index = System.Array.FindIndex(signed.TransactionIds.ToArray(), id => request.RequestUri.Query.Contains(id, StringComparison.Ordinal));
            var transaction = block["transactions"]![index]!.DeepClone();
            if (corrupt) transaction["signature"]!["sig"] = Convert.ToBase64String(new byte[64]);
            return Task.FromResult(JsonResponse(new JsonObject { ["current-round"] = 900UL, ["transactions"] = new JsonArray(transaction) }));
        };
        fixture.Provider.GetExposedSwapTransactionIds(intent, signed.Plan).Result.Should().Equal(signed.TransactionIds);
        var recovered = (await fixture.Provider.ObserveExposedSwapStageAsync(intent, signed.Plan)).Result!;
        recovered.Observation.Verdict.Should().Be("Confirmed");
        recovered.RecoveredCandidate!.SignedPayloadJson.Should().Be(signed.SignedPayloadJson);
        corrupt = true;
        var refused = (await fixture.Provider.ObserveExposedSwapStageAsync(intent, signed.Plan)).Result!;
        refused.Observation.Verdict.Should().Be("Pending");
        refused.RecoveredCandidate.Should().BeNull();
        fixture.Rpc.Posts.Should().Be(0);
        fixture.Custody.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExposedExpiryProofRetainsNoSignatureAndBindsTheExactPlan()
    {
        using var fixture = new Fixture();
        var (intent, signed) = await SignedSwapAsync(fixture);
        fixture.Rpc.LastRound = 1901;
        fixture.Rpc.Indexed = (request, _) => Task.FromResult(JsonResponse(request.RequestUri!.AbsolutePath.StartsWith("/v2/blocks/", StringComparison.Ordinal)
            ? new JsonObject { ["round"] = 1900UL, ["genesis-hash"] = RpcHandler.Genesis }
            : new JsonObject { ["current-round"] = 1900UL, ["transactions"] = new JsonArray() }));
        var result = (await fixture.Provider.ObserveExposedSwapStageAsync(intent, signed.Plan)).Result!;
        result.Observation.Verdict.Should().Be("ExpiredAbsent");
        result.RecoveredCandidate.Should().BeNull();
        fixture.Provider.ValidateExposedSwapNoEffect(intent, signed.Plan, result.Observation).Should().BeTrue();
        var continuation = await fixture.Provider.GetNextSwapStageAsync(intent,
            [new SwapStageHistory(null, result.Observation, ExposedPlan: signed.Plan)]);
        continuation.IsError.Should().BeFalse();
        continuation.Result!.Stage.Should().Be(signed.Plan.Stage);
        continuation.Result.Complete.Should().BeFalse();
        var evidence = JsonNode.Parse(result.Observation.EvidenceJson!)!.AsObject();
        evidence["planHash"] = new string('f', 64);
        fixture.Provider.ValidateExposedSwapNoEffect(intent, signed.Plan,
            result.Observation with { EvidenceJson = evidence.ToJsonString() }).Should().BeFalse();
        (await fixture.Provider.GetNextSwapStageAsync(intent,
            [new SwapStageHistory(null, result.Observation with { EvidenceJson = evidence.ToJsonString() }, ExposedPlan: signed.Plan)]))
            .IsError.Should().BeTrue();
        fixture.Provider.ValidateExposedSwapNoEffect(intent, signed.Plan,
            result.Observation with { NetworkFeePaid = "1" }).Should().BeFalse();
        fixture.Rpc.Posts.Should().Be(0);
        fixture.Custody.VerifyNoOtherCalls();
    }
}
