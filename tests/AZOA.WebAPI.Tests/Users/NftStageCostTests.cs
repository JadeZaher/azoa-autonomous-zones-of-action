using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Users;

/// <summary>F-M11: recipient-stage Solana rent and fees need the paying participant's exact accepted quote before any signing.</summary>
public sealed class NftStageCostTests
{
    [Fact]
    public async Task A_transfer_parks_unsigned_until_its_payer_accepts_the_exact_stage_quote_then_proceeds_with_that_receipt()
    {
        var (h, actor, _, id) = await SolanaTransferAsync();
        var signatures = h.Prepared.Count;

        var parked = (await h.Service.GetAsync(actor, id)).Result!;
        parked.State.Should().Be("Preparing");
        parked.NeedsStageCosts.Should().BeTrue();
        parked.StageCostPayerUserId.Should().Be(actor, "the sender's message creates the recipient's holding");
        (await h.Service.RefreshAsync(actor, id, false)).Result!.NeedsStageCosts.Should().BeTrue();
        h.Prepared.Should().HaveCount(signatures, "no acceptance, no signature");

        var stage = Review(parked);
        var quote = (await h.Service.QuoteStageCostsAsync(id, stage, NftLifecycleHarness.Credential(actor))).Result!;
        quote.Stage.Should().Be("Transfer");
        quote.ActorUserId.Should().Be(actor);
        h.Prepared.Should().HaveCount(signatures, "quotation never signs");
        var accepted = (await h.Service.AcceptStageCostsAsync(id, new(stage, NftProtocolCostOffer.From(quote).Authorization),
            NftLifecycleHarness.Credential(actor))).Result!;
        accepted.NeedsStageCosts.Should().BeFalse();
        accepted.AcceptedStageCostLimits.Should().Be(quote.Limits, "the owner sees exactly what was accepted");
        h.Prepared.Should().HaveCount(signatures, "accepting records permission only");

        var completed = await h.Service.RefreshAsync(actor, id, false);

        completed.Result!.State.Should().Be("Completed");
        h.Prepared.Last().ProtocolCostQuote.Should().Be(quote, "the signed stage carries the accepted receipt the provider enforces");
    }

    [Theory]
    [InlineData("wrong-payer")]
    [InlineData("stale-review")]
    [InlineData("widened-limits")]
    [InlineData("other-lifecycle-offer")]
    [InlineData("expired-offer")]
    public async Task Missing_stale_or_mismatched_acceptance_is_refused_before_any_chain_call(string change)
    {
        var (h, actor, recipient, id) = await SolanaTransferAsync();
        var stage = Review((await h.Service.GetAsync(actor, id)).Result!);
        var payer = NftLifecycleHarness.Credential(actor);
        var quote = (await h.Service.QuoteStageCostsAsync(id, stage, payer)).Result!;
        var authorization = NftProtocolCostOffer.From(quote).Authorization;
        var request = new NftStageCostAcceptance(stage, authorization);
        var credential = payer;
        switch (change)
        {
            case "wrong-payer":
                credential = NftLifecycleHarness.Credential(recipient);
                (await h.Service.QuoteStageCostsAsync(id, stage, credential)).IsError.Should().BeTrue("only the fee payer may review");
                break;
            case "stale-review":
                request = request with { Stage = stage with { ExpectedVersion = stage.ExpectedVersion - 1 } };
                break;
            case "widened-limits":
                request = request with { Costs = authorization with { Limits = authorization.Limits with { RetainedRent = "999999999" } } };
                break;
            case "other-lifecycle-offer":
                var (_, _, otherId) = await SecondTransferAsync(h, actor);
                var other = (await h.Service.QuoteStageCostsAsync(otherId, Review((await h.Service.GetAsync(actor, otherId)).Result!), payer)).Result!;
                request = request with { Costs = NftProtocolCostOffer.From(other).Authorization };
                break;
            case "expired-offer":
                h.Clock.Offset = TimeSpan.FromMinutes(6);
                break;
        }
        var signatures = h.Prepared.Count;
        h.CostModule.Invocations.Clear();

        var refused = await h.Service.AcceptStageCostsAsync(id, request, credential);

        refused.IsError.Should().BeTrue(change);
        (await h.Store.GetAsync(id))!.StageCostConsentsJson.Should().BeNull("nothing is recorded");
        (await h.Service.RefreshAsync(actor, id, false)).Result!.NeedsStageCosts.Should().BeTrue();
        h.Prepared.Should().HaveCount(signatures, change);
        h.CostModule.Invocations.Should().BeEmpty("a refused or absent acceptance reaches neither quotation nor signing");
    }

    [Fact]
    public async Task A_cost_increase_after_acceptance_requires_a_fresh_quote_which_then_supersedes_the_first()
    {
        var (h, actor, _, id) = await SolanaTransferAsync();
        var payer = NftLifecycleHarness.Credential(actor);
        var first = await AcceptAsync(h, actor, id, payer);
        var signatures = h.Prepared.Count;
        Estimate(h, networkFee: "6000");

        var changed = await h.Service.RefreshAsync(actor, id, false);

        changed.IsError.Should().BeTrue();
        changed.Code.Should().Be("NFT_STAGE_COSTS_CHANGED");
        h.Prepared.Should().HaveCount(signatures, "the re-quote runs before custody");
        var second = await AcceptAsync(h, actor, id, payer);
        second.Limits.NetworkFee.Should().Be("6000");
        NftStageCostContract.Read((await h.Store.GetAsync(id))!).Select(item => item.Quote).Should().Equal(first, second);

        (await h.Service.RefreshAsync(actor, id, false)).Result!.State.Should().Be("Completed");
        h.Prepared.Last().ProtocolCostQuote.Should().Be(second);
    }

    [Fact]
    public async Task A_credential_recipient_pays_and_alone_accepts_its_acceptance_stage_costs()
    {
        var h = new NftLifecycleHarness("Solana");
        var issuer = Guid.NewGuid(); var recipient = Guid.NewGuid();
        h.AddWallet(issuer); h.AddWallet(recipient);
        Offers(h);
        var request = NftLifecycleHarness.MintRequest(transferable: false);
        request.ChainType = "Solana";
        request.IsSoulbound = true;
        request.ProtocolCostAuthorization = NftProtocolCostOffer.From((await h.Service.QuoteMintCostsAsync(issuer, recipient, request, "credential")).Result!).Authorization;
        var issued = (await h.Service.MintAsync(issuer, recipient, request, "credential", credential: NftLifecycleHarness.Credential(issuer))).Result!;
        issued.State.Should().Be("AwaitingRecipientAcceptance");
        var acceptance = await h.Acceptance(issued.OperationId);

        var parked = (await h.Service.RefreshAsync(recipient, issued.OperationId, true, credential: NftLifecycleHarness.Credential(recipient),
            acceptance: acceptance)).Result!;

        parked.Stage.Should().Be("Accept");
        parked.NeedsStageCosts.Should().BeTrue();
        parked.StageCostPayerUserId.Should().Be(recipient, "the recipient's own message creates and funds its holding");
        var signatures = h.Prepared.Count;
        (await h.Service.QuoteStageCostsAsync(issued.OperationId, Review(parked), NftLifecycleHarness.Credential(issuer)))
            .IsError.Should().BeTrue("issuer consent is never reconstructed as recipient consent");
        var quote = await AcceptAsync(h, recipient, issued.OperationId, NftLifecycleHarness.Credential(recipient));
        h.Prepared.Should().HaveCount(signatures);

        var accepted = await h.Service.RefreshAsync(recipient, issued.OperationId, true, credential: NftLifecycleHarness.Credential(recipient),
            acceptance: acceptance);

        accepted.Result!.Stage.Should().Be("Deliver");
        h.Prepared.Last().Kind.Should().Be("Accept");
        h.Prepared.Last().ProtocolCostQuote.Should().Be(quote);
        (await h.Service.RefreshAsync(issuer, issued.OperationId, false)).Result!.State.Should().Be("Completed");
    }

    private static async Task<NftProtocolCostQuote> AcceptAsync(NftLifecycleHarness h, Guid payer, string id,
        AZOA.WebAPI.Models.Signing.SigningCredentialWitness credential)
    {
        var stage = Review((await h.Service.GetAsync(payer, id)).Result!);
        var quote = (await h.Service.QuoteStageCostsAsync(id, stage, credential)).Result!;
        var accepted = await h.Service.AcceptStageCostsAsync(id, new(stage, NftProtocolCostOffer.From(quote).Authorization), credential);
        accepted.IsError.Should().BeFalse(accepted.Message);
        return quote;
    }

    private static NftStageAcceptance Review(NftLifecycleView view)
        => new(Guid.NewGuid(), view.Version, view.State, view.Stage, view.PreparationGeneration);

    private static async Task<(NftLifecycleHarness Harness, Guid Actor, Guid Recipient, string Id)> SolanaTransferAsync()
    {
        var h = new NftLifecycleHarness("Solana");
        var actor = Guid.NewGuid();
        h.AddWallet(actor);
        Offers(h);
        var (recipient, _, id) = await SecondTransferAsync(h, actor);
        return (h, actor, recipient, id);
    }

    /// <summary>Mints a fresh Solana NFT for <paramref name="actor"/> and admits its transfer to a new custodial recipient.</summary>
    private static async Task<(Guid Recipient, Guid NftId, string Id)> SecondTransferAsync(NftLifecycleHarness h, Guid actor)
    {
        var key = Guid.NewGuid().ToString("N");
        var request = NftLifecycleHarness.MintRequest();
        request.ChainType = "Solana";
        request.ProtocolCostAuthorization = NftProtocolCostOffer.From((await h.Service.QuoteMintCostsAsync(actor, actor, request, key)).Result!).Authorization;
        var minted = (await h.Service.MintAsync(actor, actor, request, key, credential: NftLifecycleHarness.Credential(actor))).Result!;
        minted.State.Should().Be("Completed");
        var recipient = Guid.NewGuid();
        var wallet = h.AddWallet(recipient);
        var moved = await h.Service.MoveAsync(actor, minted.NftId, wallet.Address, false, "move-" + key, credential: NftLifecycleHarness.Credential(actor));
        moved.IsError.Should().BeFalse(moved.Message);
        moved.Result!.Stage.Should().Be("Transfer");
        return (recipient, minted.NftId, moved.Result.OperationId);
    }

    /// <summary>Offers are recorded and read back for whichever payer and tenant they were issued to.</summary>
    private static void Offers(NftLifecycleHarness h)
    {
        var quotes = new Dictionary<string, NftProtocolCostQuote>();
        Estimate(h, networkFee: "5000");
        h.ProtocolCosts.Setup(store => store.ClaimAsync(It.IsAny<NftProtocolCostQuote>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NftProtocolCostQuote quote, CancellationToken _) => quotes[quote.QuoteId] = quote);
        h.ProtocolCosts.Setup(store => store.GetAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string quoteId, Guid user, Guid tenant, CancellationToken _) =>
                quotes.GetValueOrDefault(quoteId) is { } quote && quote.ActorUserId == user && quote.ActingTenantId == tenant ? quote : null);
    }

    private static void Estimate(NftLifecycleHarness h, string networkFee)
        => h.CostModule.Setup(module => module.QuoteNftProtocolCostsAsync(It.IsAny<NftTransactionIntent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NftTransactionIntent intent, CancellationToken _) => AZOAResult<NftProtocolCostEstimate>.Success(
                intent.Kind == NftTransactionKinds.Create
                    ? new("genesis", "MetaplexNonFungible", "{\"policy\":1}", new(networkFee, "9646560", "10000000"))
                    : new("genesis", "MetaplexNonFungible", "{\"holding\":1}", new(networkFee, "2039280", "0"))));
}
