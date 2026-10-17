using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Users;

/// <summary>Replacement Solana creation generations need their own explicitly accepted cost offer.</summary>
public sealed class NftReplacementCostTests
{
    [Fact]
    public async Task ReplacementGenerationRequiresItsOwnAcceptedOfferAndKeepsTheOriginalCommand()
    {
        var (h, actor, id) = await ExpiredSolanaMint();
        var credential = NftLifecycleHarness.Credential(actor);
        var originalJson = (await h.Store.GetAsync(id))!.RequestJson;
        var originalQuote = JsonSerializer.Deserialize<NftLifecycleCommand>(originalJson)!.ProtocolCostQuote!;
        var signatures = h.Prepared.Count;
        var view = (await h.Service.GetAsync(actor, id)).Result!;
        view.NeedsReplacementCosts.Should().BeTrue();
        view.ReplacementCostsAccepted.Should().BeFalse();

        var withoutConsent = await h.Service.RefreshAsync(actor, id, false);
        withoutConsent.Message.Should().Contain("explicitly accepted protocol cost quote");
        h.Prepared.Should().HaveCount(signatures);

        var stale = new NftStageAcceptance(Guid.NewGuid(), view.Version - 1, view.State, view.Stage, view.PreparationGeneration);
        (await h.Service.QuoteReplacementCostsAsync(id, stale, credential)).IsError.Should().BeTrue();
        var stage = Review(view);
        var quote = (await h.Service.QuoteReplacementCostsAsync(id, stage, credential)).Result!;
        quote.PreparationGeneration.Should().Be(1);
        quote.QuoteId.Should().NotBe(originalQuote.QuoteId);
        h.Prepared.Should().HaveCount(signatures, "quotation never signs");

        var authorization = NftProtocolCostOffer.From(quote).Authorization;
        var widened = new NftReplacementCostAcceptance(stage, authorization with { Limits = authorization.Limits with { NetworkFee = "999999" } });
        (await h.Service.AcceptReplacementCostsAsync(id, widened, credential)).IsError.Should().BeTrue();
        (await h.Store.GetAsync(id))!.ReplacementCostConsentsJson.Should().Be("[]");

        var request = new NftReplacementCostAcceptance(stage, authorization);
        var accepted = (await h.Service.AcceptReplacementCostsAsync(id, request, credential)).Result!;
        accepted.ReplacementCostsAccepted.Should().BeTrue();
        accepted.NeedsReplacementCosts.Should().BeFalse();
        h.Prepared.Should().HaveCount(signatures, "accepting costs records permission only");
        (await h.Service.AcceptReplacementCostsAsync(id, request, credential)).IsError.Should().BeFalse("an exact saved request replays");
        (await h.Service.AcceptReplacementCostsAsync(id, request with { Costs = authorization with { QuoteHash = new string('0', 64) } },
            credential)).IsError.Should().BeTrue("a request ID cannot be rebound to different costs");

        var saved = (await h.Store.GetAsync(id))!;
        saved.RequestJson.Should().Be(originalJson);
        NftReplacementCostContract.Read(saved).Should().ContainSingle().Which.Quote.Should().Be(quote);

        (await h.Service.RefreshAsync(actor, id, false)).IsError.Should().BeFalse();
        h.Prepared.Should().HaveCount(signatures + 1);
        var replacement = h.Prepared.Last();
        replacement.ProtocolCostQuote.Should().Be(quote);
        replacement.Authorization!.PreparationGeneration.Should().Be(1);
        (await h.Service.ReconcileReplacementCostsAsync(id, request, credential)).Result!.Status.Should().Be("Applied");
    }

    [Fact]
    public async Task RetirementFencesAnUnansweredAcceptanceAndAFreshOfferNeedsNewConsent()
    {
        var (h, actor, id) = await ExpiredSolanaMint();
        var credential = NftLifecycleHarness.Credential(actor);
        var stage = Review((await h.Service.GetAsync(actor, id)).Result!);
        var quote = (await h.Service.QuoteReplacementCostsAsync(id, stage, credential)).Result!;
        var pending = new NftReplacementCostAcceptance(stage, NftProtocolCostOffer.From(quote).Authorization);
        (await h.Service.ReconcileReplacementCostsAsync(id, pending, credential)).Result!.Status.Should().Be("Unresolved");

        (await h.Service.RetireReplacementCostsAsync(id, pending, credential)).Result!.Status.Should().Be("Obsolete");
        (await h.Service.RetireReplacementCostsAsync(id, pending, credential)).Result!.Status.Should().Be("Obsolete",
            "a lost retirement response replays the same saved request");
        (await h.Service.AcceptReplacementCostsAsync(id, pending, credential)).IsError.Should().BeTrue();
        var retired = (await h.Store.GetAsync(id))!;
        NftReplacementCostContract.Read(retired).Should().BeEmpty();
        NftReplacementCostContract.Retirements(retired).Should().ContainSingle().Which.Request.Should().Be(pending);

        var signatures = h.Prepared.Count;
        (await h.Service.RefreshAsync(actor, id, false)).IsError.Should().BeTrue();
        h.Prepared.Should().HaveCount(signatures);
        var fresh = Review((await h.Service.GetAsync(actor, id)).Result!);
        fresh.ExpectedVersion.Should().BeGreaterThan(stage.ExpectedVersion);
        var renewed = (await h.Service.QuoteReplacementCostsAsync(id, fresh, credential)).Result!;
        var accepted = await h.Service.AcceptReplacementCostsAsync(id, new(fresh, NftProtocolCostOffer.From(renewed).Authorization), credential);
        accepted.Result!.ReplacementCostsAccepted.Should().BeTrue();
    }

    [Fact]
    public async Task AcceptedGenerationCannotAuthorizeALaterGeneration()
    {
        var (h, actor, id) = await ExpiredSolanaMint();
        var credential = NftLifecycleHarness.Credential(actor);
        var stage = Review((await h.Service.GetAsync(actor, id)).Result!);
        var quote = (await h.Service.QuoteReplacementCostsAsync(id, stage, credential)).Result!;
        var request = new NftReplacementCostAcceptance(stage, NftProtocolCostOffer.From(quote).Authorization);
        await h.Service.AcceptReplacementCostsAsync(id, request, credential);
        await h.Service.RefreshAsync(actor, id, false);
        h.ObservationOverride = "ExpiredAbsent";
        (await h.Service.ReconcileAsync(id)).Result!.PreparationGeneration.Should().Be(2);
        h.ObservationOverride = null;

        var signatures = h.Prepared.Count;
        var replay = await h.Service.AcceptReplacementCostsAsync(id, request, credential);
        replay.Result!.NeedsReplacementCosts.Should().BeTrue("the generation-1 receipt does not carry into generation 2");
        (await h.Service.RefreshAsync(actor, id, false)).IsError.Should().BeTrue();
        h.Prepared.Should().HaveCount(signatures);
        NftReplacementCostContract.Current((await h.Store.GetAsync(id))!).Should().BeNull();
    }

    [Fact]
    public async Task OnlyTheOriginalActorTenantAndPayerWalletCanUseReplacementCosts()
    {
        var (h, actor, id) = await ExpiredSolanaMint();
        var stage = Review((await h.Service.GetAsync(actor, id)).Result!);
        var stranger = NftLifecycleHarness.Credential(Guid.NewGuid());
        var otherTenant = NftLifecycleHarness.Credential(actor) with { TenantId = Guid.NewGuid() };
        (await h.Service.QuoteReplacementCostsAsync(id, stage, stranger)).IsError.Should().BeTrue();
        (await h.Service.QuoteReplacementCostsAsync(id, stage, otherTenant)).IsError.Should().BeTrue();
        var quote = (await h.Service.QuoteReplacementCostsAsync(id, stage, NftLifecycleHarness.Credential(actor))).Result!;
        var request = new NftReplacementCostAcceptance(stage, NftProtocolCostOffer.From(quote).Authorization);
        (await h.Service.AcceptReplacementCostsAsync(id, request, stranger)).IsError.Should().BeTrue();
        (await h.Service.RetireReplacementCostsAsync(id, request, otherTenant)).IsError.Should().BeTrue();

        var moved = Mock.Of<IWallet>(wallet => wallet.Id == quote.PayerWalletId && wallet.UserId == actor
            && wallet.ChainType == "Solana" && wallet.Address == "moved-address");
        h.Wallets.Setup(store => store.GetByIdAsync(quote.PayerWalletId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IWallet>.Success(moved));
        (await h.Service.AcceptReplacementCostsAsync(id, request, NftLifecycleHarness.Credential(actor))).IsError.Should().BeTrue();
        (await h.Store.GetAsync(id))!.ReplacementCostConsentsJson.Should().Be("[]");
    }

    [Fact]
    public async Task AcceptedReplacementSurvivesACrashBeforeCustodyWithoutAnotherAcceptance()
    {
        var (h, actor, id) = await ExpiredSolanaMint();
        var credential = NftLifecycleHarness.Credential(actor);
        var stage = Review((await h.Service.GetAsync(actor, id)).Result!);
        var quote = (await h.Service.QuoteReplacementCostsAsync(id, stage, credential)).Result!;
        await h.Service.AcceptReplacementCostsAsync(id, new(stage, NftProtocolCostOffer.From(quote).Authorization), credential);
        var signatures = h.Prepared.Count;
        h.BeforePrepare = _ => throw new HttpRequestException("Crash before custody");
        await Assert.ThrowsAsync<HttpRequestException>(() => h.Service.RefreshAsync(actor, id, false));
        h.BeforePrepare = null;

        (await h.Service.RefreshAsync(actor, id, false)).IsError.Should().BeFalse();

        h.Prepared.Should().HaveCount(signatures + 1);
        h.Prepared.Last().ProtocolCostQuote.Should().Be(quote);
        NftReplacementCostContract.Read((await h.Store.GetAsync(id))!).Should().ContainSingle();
    }

    [Fact]
    public async Task RotatedCredentialPlatformSignerCannotBeQuotedForAReplacementGeneration()
    {
        var (h, actor, id) = await ExpiredSolanaMint(credential: true);
        var stage = Review((await h.Service.GetAsync(actor, id)).Result!);
        h.Module.SetupGet(module => module.CredentialAuthorityAddress).Returns("rotated-platform");

        var refused = await h.Service.QuoteReplacementCostsAsync(id, stage, NftLifecycleHarness.Credential(actor));

        refused.IsError.Should().BeTrue();
        refused.Message.Should().Contain("platform signer changed");
        h.ProtocolCosts.Verify(store => store.ClaimAsync(It.IsAny<NftProtocolCostQuote>(), It.IsAny<CancellationToken>()), Times.Once,
            "only the original issuance offer was ever recorded");
    }

    private static NftStageAcceptance Review(NftLifecycleView view)
        => new(Guid.NewGuid(), view.Version, view.State, view.Stage, view.PreparationGeneration);

    internal static async Task<(NftLifecycleHarness Harness, Guid Actor, string Id)> ExpiredSolanaMint(bool credential = false)
    {
        var h = new NftLifecycleHarness("Solana") { Confirm = false };
        var actor = Guid.NewGuid();
        var recipient = credential ? Guid.NewGuid() : actor;
        h.AddWallet(actor);
        if (credential) h.AddWallet(recipient);
        SolanaCosts(h, actor);
        var request = NftLifecycleHarness.MintRequest(transferable: !credential);
        request.ChainType = "Solana";
        request.IsSoulbound = credential;
        var offer = await h.Service.QuoteMintCostsAsync(actor, recipient, request, "replacement");
        request.ProtocolCostAuthorization = NftProtocolCostOffer.From(offer.Result!).Authorization;
        var minted = await h.Service.MintAsync(actor, recipient, request, "replacement", credential: NftLifecycleHarness.Credential(actor));
        minted.Result!.State.Should().Be("Submitted");
        h.ObservationOverride = "ExpiredAbsent";
        var expired = await h.Service.ReconcileAsync(minted.Result.OperationId);
        h.ObservationOverride = null;
        expired.Result!.State.Should().Be("Preparing");
        expired.Result.PreparationGeneration.Should().Be(1);
        return (h, actor, minted.Result.OperationId);
    }

    internal static Dictionary<string, NftProtocolCostQuote> SolanaCosts(NftLifecycleHarness h, Guid actor)
    {
        var quotes = new Dictionary<string, NftProtocolCostQuote>();
        h.CostModule.Setup(module => module.QuoteNftProtocolCostsAsync(It.IsAny<NftTransactionIntent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<NftProtocolCostEstimate>.Success(new("genesis", "MetaplexNonFungible", "{\"policy\":1}",
                new("5000", "9646560", "10000000"))));
        h.ProtocolCosts.Setup(store => store.ClaimAsync(It.IsAny<NftProtocolCostQuote>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NftProtocolCostQuote quote, CancellationToken _) => quotes[quote.QuoteId] = quote);
        h.ProtocolCosts.Setup(store => store.GetAsync(It.IsAny<string>(), actor, Guid.Empty, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string quoteId, Guid _, Guid _, CancellationToken _) => quotes.GetValueOrDefault(quoteId));
        return quotes;
    }
}
