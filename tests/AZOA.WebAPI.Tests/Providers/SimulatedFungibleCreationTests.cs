using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Simulated;
using AZOA.WebAPI.Services.Reserve;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Providers;

/// <summary>Simulated fungible creation through the factory-resolved module seam (quote → prepare → submit → observe).</summary>
public sealed class SimulatedFungibleCreationTests
{
    private static BlockchainProviderFactory SimulatedFactory()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Blockchain:Mode"] = "Simulated" })
            .Build();
        BlockchainProviderRegistration[] registrations =
            [new("Simulated", () => new SimulatedBlockchainProvider(config, NullLogger<SimulatedBlockchainProvider>.Instance))];
        return new BlockchainProviderFactory(registrations, config);
    }

    private static IFungibleCreationModule Module(BlockchainProviderFactory factory)
    {
        factory.GetProvider("Algorand", ChainNetwork.Devnet).TryGetModule<IFungibleCreationModule>(out var module).Should().BeTrue();
        return module!;
    }

    private static FungibleCreationIntent Intent(string total = "100000000000") => new("op-key", 0, Guid.NewGuid(), null,
        "Simulated", ChainNetwork.Devnet, "sim:simulated:reserve", SigningContext.PlatformFor("Simulated") with { Scope = AzoaScopes.TokenCreateSign },
        "Solar Commons Stake", "SOLR", total, 6, false,
        "sim:simulated:reserve", "sim:simulated:reserve", "sim:simulated:reserve", "sim:simulated:reserve");

    private static async Task<FungibleCreationIntent> Quoted(IFungibleCreationModule module, FungibleCreationIntent intent)
    {
        var quote = await module.QuoteFungibleCreationAsync(intent);
        quote.IsError.Should().BeFalse(quote.Message);
        return intent with { Quote = quote.Result };
    }

    [Fact]
    public async Task CreationReachesConfirmedEvidenceWithStableAssetIdentityOnlyAfterSubmission()
    {
        var factory = SimulatedFactory();
        var module = Module(factory);
        var intent = await Quoted(module, Intent());
        intent.Quote!.NetworkFeeBaseUnits.Should().Be("0");

        var prepared = (await module.PrepareFungibleCreationAsync(intent)).Result!;
        (await module.PrepareFungibleCreationAsync(intent)).Result.Should().BeEquivalentTo(prepared);
        prepared.TransactionId.Should().StartWith(SimulatedBlockchainProvider.SimTxPrefix);
        prepared.ProtocolIdentity.Should().Be(intent.Quote.ProtocolIdentity);

        var beforeSubmit = (await module.ObserveFungibleCreationAsync(prepared)).Result!;
        beforeSubmit.Verdict.Should().Be("Pending");
        beforeSubmit.AssetId.Should().BeNull();

        // Round-trips through JSON the way FungibleCreationService persists PreparedJson.
        var persisted = JsonSerializer.Deserialize<PreparedFungibleCreation>(JsonSerializer.Serialize(prepared))!;
        await module.SubmitFungibleCreationAsync(persisted);
        await module.SubmitFungibleCreationAsync(persisted);
        var first = (await module.ObserveFungibleCreationAsync(persisted)).Result!;
        var second = (await module.ObserveFungibleCreationAsync(prepared)).Result!;

        first.Verdict.Should().Be("Confirmed");
        first.TransactionId.Should().Be(prepared.TransactionId);
        ulong.Parse(first.AssetId!, CultureInfo.InvariantCulture).Should().BePositive();
        first.EvidenceJson.Should().NotBeNullOrWhiteSpace();
        second.Should().Be(first);
        SimulatedBlockchainProvider.SimFungibleAssetId(prepared.PreparationId).Should().Be(first.AssetId);
    }

    [Fact]
    public async Task TamperedCandidateOrForeignQuoteIsRefused()
    {
        var module = Module(SimulatedFactory());
        var intent = await Quoted(module, Intent());
        var prepared = (await module.PrepareFungibleCreationAsync(intent)).Result!;
        var tampered = prepared with { TransactionId = SimulatedBlockchainProvider.SimTxPrefix + "other" };

        (await module.ObserveFungibleCreationAsync(tampered)).IsError.Should().BeTrue();
        await module.Invoking(m => m.SubmitFungibleCreationAsync(tampered)).Should().ThrowAsync<InvalidOperationException>();
        (await module.PrepareFungibleCreationAsync(intent with { TotalBaseUnits = "1" })).IsError.Should().BeTrue();
        (await module.PrepareFungibleCreationAsync(intent with { Network = ChainNetwork.Testnet })).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ManagerAdmitsAlgorandRequestOnTheSimulatedProviderInSimulatedMode()
    {
        var harness = new FungibleAdmissionHarness(ReserveMintTargets.CallerWallet, providerFactory: SimulatedFactory(), walletChainType: "Simulated");
        var result = await harness.AdmitAsync();

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Operation!.State.Should().Be("AwaitingConsent");
        harness.Commands.Values.Single().Intent.ChainType.Should().Be("Simulated");
    }

    /// <summary>The shipped default target (PlatformReserve) on the dev node: the real reserve resolves the Simulated chain and becomes creator and every authority.</summary>
    [Fact]
    public async Task ManagerAdmitsIntoTheSimulatedPlatformReserveUnderTheDevelopmentConfiguration()
    {
        var config = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: false).Build();
        var options = config.GetSection(ReserveOptions.SectionName).Get<ReserveOptions>()!;
        var consent = new Moq.Mock<AZOA.WebAPI.Interfaces.Managers.ITenantConsentGate>();
        consent.Setup(gate => gate.EnsureAllowedAsync(Moq.It.IsAny<SigningContext>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOA.WebAPI.Models.Responses.AZOAResult<bool>.Success(true));
        var custody = new AZOA.WebAPI.Services.Custody.KeyCustodyService(Moq.Mock.Of<AZOA.WebAPI.Interfaces.Stores.IWalletStore>(),
            new AZOA.WebAPI.Services.Signing.WalletKeyService(config), config, consent.Object);
        var reserve = new PlatformReserveService(custody, Moq.Mock.Of<IBlockchainProviderFactory>(),
            Moq.Mock.Of<IPlatformReserveLedgerStore>(), options);
        var harness = new FungibleAdmissionHarness(ReserveMintTargets.PlatformReserve, reserve: reserve,
            providerFactory: SimulatedFactory(), walletChainType: "Simulated");

        var result = await harness.AdmitAsync();

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Operation!.State.Should().Be("AwaitingConsent");
        var intent = harness.Commands.Values.Single().Intent;
        intent.ChainType.Should().Be("Simulated");
        intent.SigningContext.IsPlatform.Should().BeTrue();
        intent.CreatorAddress.Should().Be(options.ExpectedAddresses["Simulated"]).And.StartWith(SimulatedBlockchainProvider.SimPrefix);
        new[] { intent.ManagerAddress, intent.ReserveAddress, intent.FreezeAddress, intent.ClawbackAddress }
            .Should().AllBe(intent.CreatorAddress);
    }
}
