using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Users;

/// <summary>The selected Solana creation receipt is admitted once and then followed, never re-selected.</summary>
public sealed class NftProtocolCostSelectionTests
{
    [Fact]
    public async Task AcceptedReceiptIsPersistedWithTheCommandAndReachesTheFirstPreparation()
    {
        var h = new NftLifecycleHarness("Solana") { Confirm = false };
        var actor = Guid.NewGuid();
        h.AddWallet(actor);
        NftReplacementCostTests.SolanaCosts(h, actor);
        var request = SolanaDraft();
        var unaccepted = await h.Service.MintAsync(actor, actor, request, "selected", credential: NftLifecycleHarness.Credential(actor));
        unaccepted.IsError.Should().BeTrue("Solana creation needs an explicitly accepted receipt");
        var quote = (await h.Service.QuoteMintCostsAsync(actor, actor, request, "selected")).Result!;
        request.ProtocolCostAuthorization = NftProtocolCostOffer.From(quote).Authorization;

        var minted = await h.Service.MintAsync(actor, actor, request, "selected", credential: NftLifecycleHarness.Credential(actor));

        var command = JsonSerializer.Deserialize<NftLifecycleCommand>((await h.Store.GetAsync(minted.Result!.OperationId))!.RequestJson)!;
        command.ProtocolCostQuote.Should().Be(quote);
        var prepared = h.Prepared.Should().ContainSingle().Subject;
        prepared.ProtocolCostQuote.Should().Be(quote);
        prepared.Authorization!.PreparationGeneration.Should().Be(0);
    }

    [Fact]
    public async Task AdmittedRequestReplaysAfterItsOfferDisappearsAndResumesTheSameReceiptAfterACrash()
    {
        var h = new NftLifecycleHarness("Solana") { Confirm = false };
        var actor = Guid.NewGuid();
        h.AddWallet(actor);
        var quotes = NftReplacementCostTests.SolanaCosts(h, actor);
        var request = SolanaDraft();
        var quote = (await h.Service.QuoteMintCostsAsync(actor, actor, request, "crash")).Result!;
        request.ProtocolCostAuthorization = NftProtocolCostOffer.From(quote).Authorization;
        h.BeforePrepare = _ => throw new HttpRequestException("Crash before custody");
        await Assert.ThrowsAsync<HttpRequestException>(() => h.Service.MintAsync(actor, actor, request, "crash",
            credential: NftLifecycleHarness.Credential(actor)));
        h.BeforePrepare = null;
        quotes.Clear();

        var resumed = await h.Service.MintAsync(actor, actor, request, "crash", credential: NftLifecycleHarness.Credential(actor));

        resumed.IsError.Should().BeFalse("admission, not later preparation, is the receipt expiry boundary");
        h.Prepared.Should().ContainSingle("the crash happened before custody").Which.ProtocolCostQuote.Should().Be(quote);
        h.ProtocolCosts.Verify(store => store.GetAsync(quote.QuoteId, actor, Guid.Empty, It.IsAny<CancellationToken>()), Times.Once,
            "replay resolves the original admission before reading quote availability");
        var different = SolanaDraft();
        different.ProtocolCostAuthorization = request.ProtocolCostAuthorization! with { QuoteHash = new string('0', 64) };
        (await h.Service.MintAsync(actor, actor, different, "crash", credential: NftLifecycleHarness.Credential(actor)))
            .IsError.Should().BeTrue("another receipt cannot replace the admitted one under the same key");
    }

    [Fact]
    public async Task HistoricalRowsWithoutCostFieldsKeepTheirSerializedBytes()
    {
        var h = new NftLifecycleHarness();
        var actor = Guid.NewGuid();
        h.AddWallet(actor);
        var minted = await h.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(), "historical",
            credential: NftLifecycleHarness.Credential(actor));
        var row = (await h.Store.GetAsync(minted.Result!.OperationId))!;
        var legacy = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)! with { ActorAuthority = null };
        var legacyJson = JsonSerializer.Serialize(legacy);
        var intent = h.Prepared.Single() with { ProtocolCostQuote = null };
        var intentJson = JsonSerializer.Serialize(intent);
        var quote = new NftProtocolCostQuote(Guid.NewGuid().ToString("N"), actor, Guid.Empty, Guid.NewGuid(), "payer", "Solana",
            ChainNetwork.Devnet, "solana:genesis", "lifecycle", new string('a', 64), "Create", 0, "MetaplexNonFungible",
            "{\"policy\":1}", new("1", "2", "3"), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5));

        foreach (var added in new[] { "ProtocolCostQuote", "WorkflowCostAcceptance", "ActorAuthority", "PlatformAuthorityAddress", "ProtocolCostAuthorization" })
            legacyJson.Should().NotContain($"\"{added}\"", "absent round-3 fields must not change historical command bytes");
        intentJson.Should().NotContain("\"ProtocolCostQuote\"", "persisted pre-cost intents are re-digested during wire recovery");
        JsonSerializer.Serialize(quote).Should().NotContain("\"QuotedIntentHash\"");
        JsonSerializer.Serialize(JsonSerializer.Deserialize<NftLifecycleCommand>(legacyJson)).Should().Be(legacyJson);
        JsonSerializer.Serialize(JsonSerializer.Deserialize<NftTransactionIntent>(intentJson)).Should().Be(intentJson);
    }

    private static UserNFTMintModel SolanaDraft()
    {
        var request = NftLifecycleHarness.MintRequest();
        request.ChainType = "Solana";
        return request;
    }
}
