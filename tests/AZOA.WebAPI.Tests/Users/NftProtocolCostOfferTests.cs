using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Users;

public sealed class NftProtocolCostOfferTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedCredentialParticipantOrAuthorityRefusesBeforeAdmission(bool changeAuthority)
    {
        var h = new NftLifecycleHarness();
        var actor = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        h.AddWallet(actor);
        h.AddWallet(recipient);
        h.CostModule.Setup(module => module.QuoteNftProtocolCostsAsync(It.IsAny<NftTransactionIntent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<NftProtocolCostEstimate>.Success(new("fixture-genesis", "ASA", "{\"policy\":1}", new("1", "2", "3"))));
        NftProtocolCostQuote? saved = null;
        h.ProtocolCosts.Setup(store => store.ClaimAsync(It.IsAny<NftProtocolCostQuote>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NftProtocolCostQuote quote, CancellationToken _) => saved = quote);
        h.ProtocolCosts.Setup(store => store.GetAsync(It.IsAny<string>(), actor, Guid.Empty, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => saved);
        var request = NftLifecycleHarness.MintRequest(false);
        request.IsSoulbound = true;
        var offered = await h.Service.QuoteMintCostsAsync(actor, recipient, request, "changed-credential");
        offered.IsError.Should().BeFalse();
        request.ProtocolCostAuthorization = NftProtocolCostOffer.From(saved!).Authorization;
        if (changeAuthority) h.Module.SetupGet(module => module.CredentialAuthorityAddress).Returns("new-platform");
        else
        {
            var replacement = Mock.Of<IWallet>(wallet => wallet.Id == Guid.NewGuid() && wallet.UserId == recipient
                && wallet.ChainType == "Algorand" && wallet.Address == "replacement-recipient");
            h.Wallets.Setup(store => store.GetByUserAsync(recipient, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<IEnumerable<IWallet>>.Success([replacement]));
        }
        var rejected = await h.Service.MintAsync(actor, recipient, request, "changed-credential", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        rejected.IsError.Should().BeTrue();
        rejected.Message.Should().Contain("changed after quotation");
        (await h.Service.GetRequestAsync(actor, "Mint", "changed-credential")).IsError.Should().BeTrue();
        h.Prepared.Should().BeEmpty();
        h.Submitted.Should().BeEmpty();
        var replacementOffer = await h.Service.QuoteMintCostsAsync(actor, recipient, request, "changed-credential");
        replacementOffer.IsError.Should().BeFalse();
        replacementOffer.Result!.QuotedIntentHash.Should().NotBe(offered.Result!.QuotedIntentHash);
    }

    [Fact]
    public async Task QuoteDoesNotAdmitAndAcceptedMintPreparesTheSameIntent()
    {
        var h = new NftLifecycleHarness();
        var actor = Guid.NewGuid();
        h.AddWallet(actor);
        NftTransactionIntent? quotedIntent = null;
        h.CostModule.Setup(module => module.QuoteNftProtocolCostsAsync(It.IsAny<NftTransactionIntent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NftTransactionIntent intent, CancellationToken _) =>
            {
                quotedIntent = intent;
                return AZOAResult<NftProtocolCostEstimate>.Success(new("fixture-genesis", "ASA", "{\"policy\":1}", new("1", "2", "3")));
            });
        NftProtocolCostQuote? saved = null;
        h.ProtocolCosts.Setup(store => store.ClaimAsync(It.IsAny<NftProtocolCostQuote>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NftProtocolCostQuote quote, CancellationToken _) => saved = quote);
        h.ProtocolCosts.Setup(store => store.GetAsync(It.IsAny<string>(), actor, Guid.Empty, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => saved);
        var request = NftLifecycleHarness.MintRequest();
        var result = await h.Service.QuoteMintCostsAsync(actor, actor, request, "quoted-mint");
        result.IsError.Should().BeFalse();
        saved.Should().NotBeNull();
        h.Prepared.Should().BeEmpty();
        h.Submitted.Should().BeEmpty();
        (await h.Service.GetRequestAsync(actor, "Mint", "quoted-mint")).IsError.Should().BeTrue();
        request.ProtocolCostAuthorization = NftProtocolCostOffer.From(saved!).Authorization;
        var mint = await h.Service.MintAsync(actor, actor, request, "quoted-mint", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        mint.IsError.Should().BeFalse();
        h.Prepared.First().ProtocolCostQuote.Should().Be(saved);
        (h.Prepared.First() with { ProtocolCostQuote = null }).Should().Be(quotedIntent);
        var replay = await h.Service.QuoteMintCostsAsync(actor, actor, request, "quoted-mint");
        replay.Result.Should().Be(saved);
        h.CostModule.Verify(module => module.QuoteNftProtocolCostsAsync(It.IsAny<NftTransactionIntent>(), It.IsAny<CancellationToken>()), Times.Once);
        request.Name = "Changed draft";
        (await h.Service.QuoteMintCostsAsync(actor, actor, request, "quoted-mint")).IsError.Should().BeTrue();
        saved!.DraftHash.Should().NotBe(NftProtocolCostContract.DraftHash(saved.LifecycleId, actor, actor, request));
    }
}
