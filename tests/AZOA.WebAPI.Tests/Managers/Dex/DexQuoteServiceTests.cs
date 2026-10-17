using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Dex;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using SurrealForge.Client;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class DexQuoteServiceTests
{
    [Fact]
    public async Task PersistsActorNetworkAndServerFeeWithProviderPrerequisitesInsideQuoteHash()
    {
        var fixture = new Fixture();
        var response = await fixture.Service.QuoteAsync(fixture.Credential, fixture.Request);
        response.IsError.Should().BeFalse(response.Message);
        response.Result!.Unavailable.Should().BeFalse();
        var quote = response.Result.Quote!;
        quote.Terms.PlatformFee.TreasuryAddress.Should().Be(Fixture.Treasury);
        quote.Terms.PlatformFee.Amount.Should().Be("10");
        quote.Terms.Owner.UserId.Should().Be(fixture.Credential.ActorUserId);
        quote.Terms.Network.Should().Be(fixture.Request.Network);
        quote.Terms.Prerequisites.Should().ContainSingle();
        quote.QuoteHash.Should().Be(CanonicalJson.Hash(quote.Terms));
        fixture.Saved!.QuoteHash.Should().Be(quote.QuoteHash);
        (await fixture.Service.GetAsync(quote.QuoteId, fixture.Credential with { ActorUserId = Guid.NewGuid() })).Should().BeNull();
        (await fixture.Service.GetAsync(quote.QuoteId, fixture.Credential))!.QuoteHash.Should().Be(quote.QuoteHash);
    }

    [Theory]
    [InlineData("fee")]
    [InlineData("owner")]
    [InlineData("network")]
    [InlineData("decimals")]
    public async Task ProviderCannotReplaceServerEconomicOrParticipantAuthority(string change)
    {
        var fixture = new Fixture { Change = change };
        (await fixture.Service.QuoteAsync(fixture.Credential, fixture.Request)).IsError.Should().BeTrue();
        fixture.Saved.Should().BeNull();
    }

    [Fact]
    public async Task AnotherUsersWalletCannotReachProviderAndUnavailableDoesNotPersistQuote()
    {
        var fixture = new Fixture();
        fixture.Wallet.UserId = Guid.NewGuid();
        (await fixture.Service.QuoteAsync(fixture.Credential, fixture.Request)).IsError.Should().BeTrue();
        fixture.Module.Verify(module => module.QuoteSwapAsync(It.IsAny<DexQuoteTerms>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Wallet.UserId = fixture.Credential.ActorUserId;
        fixture.Change = "unavailable";
        var result = await fixture.Service.QuoteAsync(fixture.Credential, fixture.Request);
        result.IsError.Should().BeFalse(); result.Result!.Unavailable.Should().BeTrue(); result.Result.Quote.Should().BeNull();
        fixture.Saved.Should().BeNull();
    }

    private sealed class Fixture
    {
        internal const string Treasury = "0x2222222222222222222222222222222222222222";
        internal readonly Mock<IDexTransactionModule> Module;
        internal readonly DexQuoteService Service;
        internal readonly SigningCredentialWitness Credential;
        internal readonly DexQuoteRequest Request;
        internal readonly AZOA.WebAPI.Models.Wallet Wallet;
        internal DexQuoteRecord? Saved;
        internal string Change = "";

        internal Fixture()
        {
            var actor = Guid.NewGuid();
            Wallet = new() { Id = Guid.NewGuid(), UserId = actor, ChainType = "Ethereum", Address = "0x1111111111111111111111111111111111111111" };
            Credential = new(1, "Jwt", actor, null, null, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1),
                null, null, [], true, TokenUse: AzoaClaims.TokenUseLogin);
            Request = new(Wallet.Id, new("Ethereum", "Devnet", "eip155:31337"), new("in", "IN", 18, false), new("out", "OUT", 18, false),
                "1000000000000000000000000000000", 100, new("100000", "0", "10000"));
            var wallets = new Mock<IWalletStore>(MockBehavior.Strict);
            wallets.Setup(store => store.GetByIdAsync(Wallet.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => AZOAResult<IWallet>.Success(Wallet));
            var quotes = new Mock<IDexQuoteStore>(MockBehavior.Strict);
            quotes.Setup(store => store.ClaimAsync(It.IsAny<DexQuoteRecord>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DexQuoteRecord candidate, CancellationToken _) => Saved = candidate);
            quotes.Setup(store => store.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Saved);
            var fees = new Mock<INodeFeeScheduleManager>(MockBehavior.Strict);
            fees.Setup(manager => manager.QuoteSwapBaseUnitsAsync(Request.GrossInput, It.IsAny<CancellationToken>())).ReturnsAsync(
                AZOAResult<NodeFeeQuoteResponse>.Success(new()
                {
                    Denomination = NodeFeeSchedule.InputBaseUnitsV1, FeeAmount = "10", NetAmount = "999999999999999999999999999990",
                    GrossAmount = Request.GrossInput, FlatBaseUnits = "10", Bps = 0, ScheduleVersion = 3,
                }));
            Module = new(MockBehavior.Strict);
            Module.SetupGet(module => module.ChainType).Returns("Ethereum");
            Module.SetupGet(module => module.ActiveNetwork).Returns(ChainNetwork.Devnet);
            Module.SetupGet(module => module.DexCapability).Returns(new DexCapability("fixture", "1", true, false, false, false, true, []));
            Module.Setup(module => module.QuoteSwapAsync(It.IsAny<DexQuoteTerms>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DexQuoteTerms proposed, CancellationToken _) =>
                {
                    if (Change == "unavailable") return AZOAResult<DexQuoteTerms>.FailureWithCode("Pool unavailable.", AzoaErrorCodes.DependencyUnavailable);
                    var quoted = proposed with { ExpectedOutput = "1000", MinimumOutput = "990", ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30),
                        ProtocolEvidence = JsonSerializer.SerializeToElement(new { proof = "provider-fixture" }),
                        Prerequisites = [new("Approval", proposed.InputAsset.Id, proposed.GrossInput, proposed.Owner.Address, "spender", true)] };
                    quoted = Change switch
                    {
                        "fee" => quoted with { PlatformFee = quoted.PlatformFee with { Amount = "0" } },
                        "owner" => quoted with { ActorUserId = Guid.NewGuid() },
                        "network" => quoted with { Network = quoted.Network with { ProtocolIdentity = "eip155:1" } },
                        "decimals" => quoted with { InputAsset = quoted.InputAsset with { Decimals = 6 } },
                        _ => quoted,
                    };
                    return AZOAResult<DexQuoteTerms>.Success(quoted);
                });
            var blockchain = new Mock<IBlockchainProvider>(MockBehavior.Strict);
            IValueRailProvider? rail = blockchain.Object;
            IDexTransactionModule? moduleObject = Module.Object;
            var providers = new Mock<IBlockchainProviderFactory>(MockBehavior.Strict);
            providers.Setup(factory => factory.TryGetRailProvider("Ethereum", RailEnvironment.ForChain(ChainNetwork.Devnet), out rail)).Returns(true);
            providers.Setup(factory => factory.TryGetModule(blockchain.Object, out moduleObject)).Returns(true);
            var authority = new SigningAdmissionAuthorityFactory(new Mock<IUserStore>(MockBehavior.Strict).Object, wallets.Object,
                new Mock<ISurrealExecutor>(MockBehavior.Strict).Object);
            var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddInMemoryCollection(new Dictionary<string, string?>
                { ["Dex:FeePolicies:Ethereum:Devnet:TreasuryAddress"] = Treasury }).Build();
            Service = new(quotes.Object, wallets.Object, fees.Object, authority, new(providers.Object), config);
        }
    }
}
