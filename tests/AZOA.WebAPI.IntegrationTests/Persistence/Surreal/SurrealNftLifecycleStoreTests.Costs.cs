using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealNftLifecycleStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrExpiredSelectedReceiptCannotCreateLifecycle(bool expired)
    {
        var (row, quote) = CostCandidate(expired);
        if (expired) await new SurrealNftProtocolCostQuoteStore(_executor).ClaimAsync(quote);
        (await _store.ClaimAsync(row)).Should().BeNull();
        (await _store.GetAsync(row.Id)).Should().BeNull();
    }

    [Fact]
    public async Task SelectedReceiptAdmitsOnceAndReplayDoesNotDependOnOfferAvailability()
    {
        var (row, quote) = CostCandidate();
        await new SurrealNftProtocolCostQuoteStore(_executor).ClaimAsync(quote);
        var admitted = await _store.ClaimAsync(row);
        admitted.Should().NotBeNull();
        admitted!.RequestJson.Should().Be(row.RequestJson);
        var deleted = await _executor.ExecuteAsync(SurrealWriter.DeleteOnly<NftProtocolCostQuoteRecord>(quote.QuoteId).Build());
        deleted.EnsureAllOk();
        (await _store.ClaimAsync(row))!.RequestJson.Should().Be(admitted.RequestJson);
    }

    [Fact]
    public async Task ChangedStoredEvidenceRollsBackLifecycleCreation()
    {
        var (row, quote) = CostCandidate();
        await new SurrealNftProtocolCostQuoteStore(_executor).ClaimAsync(quote);
        var altered = await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<NftProtocolCostQuoteRecord>(quote.QuoteId)
            .Set(item => item.QuoteHash, new string('f', 64)).Build());
        altered.EnsureAllOk();
        (await _store.ClaimAsync(row)).Should().BeNull();
        (await _store.GetAsync(row.Id)).Should().BeNull();
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("recipient")]
    [InlineData("payer")]
    [InlineData("draft")]
    [InlineData("generation")]
    [InlineData("tenant")]
    public async Task SelectedReceiptCannotAuthorizeChangedParticipantsOrDraft(string mutation)
    {
        var (row, quote) = CostCandidate();
        await new SurrealNftProtocolCostQuoteStore(_executor).ClaimAsync(quote);
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!;
        command = mutation switch
        {
            "actor" => command with { Actor = Guid.NewGuid() },
            "recipient" => command with { Recipient = Guid.NewGuid() },
            "payer" => command with { ActorWalletId = Guid.NewGuid() },
            "tenant" => command with { ActingTenantId = Guid.NewGuid() },
            _ => command,
        };
        if (mutation == "draft") command.Metadata.Name = "different";
        if (mutation == "generation") row.PreparationGeneration = 1;
        row.RequestJson = JsonSerializer.Serialize(command);
        row.RequestHash = NftLifecycleIdentity.Fingerprint(row.RequestJson);
        (await _store.ClaimAsync(row)).Should().BeNull();
        (await _store.GetAsync(row.Id)).Should().BeNull();
    }

    private static (NftLifecycle Row, NftProtocolCostQuote Quote) CostCandidate(bool expired = false)
    {
        var actor = Guid.NewGuid();
        var wallet = Guid.NewGuid();
        var row = Candidate();
        row.ActorUserId = row.RecipientUserId = actor.ToString("N");
        row.Operation = "Mint"; row.Stage = "Create"; row.State = "Preparing";
        row.ChainType = "Solana"; row.Network = "Devnet";
        var metadata = new UserNFTMintModel { WalletId = wallet, ChainType = "Solana", Name = "NFT", MetadataURI = "ipfs://metadata" };
        var now = DateTimeOffset.UtcNow;
        var quote = new NftProtocolCostQuote(Guid.NewGuid().ToString("N"), actor, Guid.Empty, wallet, "payer", "Solana",
            ChainNetwork.Devnet, "genesis", row.Id, NftProtocolCostContract.DraftHash(row.Id, actor, actor, metadata),
            "Create", 0, "MetaplexNonFungible", "{\"revision\":1}", new("5000", "10000000", "10000000"),
            now.AddMinutes(-10), expired ? now.AddMinutes(-1) : now.AddMinutes(5));
        metadata.ProtocolCostAuthorization = new(quote.QuoteId, NftProtocolCostContract.Hash(quote), quote.Limits);
        var command = new NftLifecycleCommand(actor, actor, wallet, wallet, "payer", "payer", metadata, -1,
            Guid.Empty, ProtocolCostQuote: quote);
        row.RequestJson = JsonSerializer.Serialize(command);
        row.RequestHash = NftLifecycleIdentity.Fingerprint(row.RequestJson);
        return (row, quote);
    }
}
