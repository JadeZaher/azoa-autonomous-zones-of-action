using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Simulated;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AZOA.WebAPI.Tests.Providers;

/// <summary>Simulated atomic exchange group through the factory-resolved, D-7-gated module seam (prepare → observe → submit → observe).</summary>
public sealed class SimulatedExchangeTests
{
    private static readonly string Reserve = SimulatedBlockchainProvider.SimAddress("Simulated", "reserve");
    private static readonly string Treasury = SimulatedBlockchainProvider.SimAddress("Simulated", "treasury");
    private static readonly string Participant = SimulatedBlockchainProvider.SimAddress("Simulated", "participant");

    /// <summary>The shipped Development configuration, so the pin under test is the one the dev node boots with.</summary>
    private static IBlockchainProvider DevelopmentProvider(bool development = true)
    {
        var builder = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: false);
        if (development) builder.AddJsonFile("appsettings.Development.json", optional: false);
        var config = builder.AddInMemoryCollection(new Dictionary<string, string?> { ["Blockchain:Mode"] = "Simulated" }).Build();
        var factory = new BlockchainProviderFactory(
            [new BlockchainProviderRegistration("Simulated", () => new SimulatedBlockchainProvider(config, NullLogger<SimulatedBlockchainProvider>.Instance))],
            config);
        return factory.GetProvider("Simulated", ChainNetwork.Devnet);
    }

    private static ExchangeWireIntent Intent(string protocolIdentity = "sim:devnet")
    {
        var participant = SigningContext.ForUser(Guid.NewGuid(), Guid.NewGuid()).ForChain("Simulated")
            .ActingAs(Guid.NewGuid(), AzoaScopes.SwapSign, Guid.NewGuid());
        var platform = SigningContext.PlatformFor("Simulated");
        return new(1, "source-1", 0, "Simulated", ChainNetwork.Devnet, protocolIdentity,
            [new("7", "10", Participant, Treasury, "Participant", participant), new("42", "400", Reserve, Participant, "Reserve", platform)],
            Reserve, platform, "10000");
    }

    private static async Task<long> Balance(IBlockchainProvider provider, string address, string token)
        => long.Parse((await provider.GetBalanceAsync(address, token)).Result!);

    [Fact]
    public async Task GroupConfirmsOnlyAfterSubmissionAndAppliesBothLegsExactlyOnce()
    {
        var provider = DevelopmentProvider();
        provider.TryGetModule<IExchangeTransactionModule>(out var module).Should().BeTrue("Development pins Exchange:Activation:Simulated:Devnet");
        var intent = Intent();

        var prepared = (await module!.PrepareExchangeAsync(intent)).Result!;
        (await module.PrepareExchangeAsync(intent)).Result!.TransactionIds.Should().Equal(prepared.TransactionIds);
        prepared.TransactionIds.Should().HaveCount(3).And.OnlyContain(id => id.StartsWith(SimulatedBlockchainProvider.SimTxPrefix));
        (await module.ObserveExchangeAsync(prepared)).Result!.Verdict.Should().Be("Unknown");
        (await Balance(provider, Participant, "42")).Should().Be(0);

        // Round-trips through JSON the way the exchange source persists PreparedWireJson.
        var persisted = JsonSerializer.Deserialize<PreparedExchangeWire>(JsonSerializer.Serialize(prepared))!;
        await module.SubmitExchangeAsync(persisted);
        await module.SubmitExchangeAsync(persisted);
        var observed = (await module.ObserveExchangeAsync(persisted)).Result!;

        observed.Verdict.Should().Be("Confirmed");
        observed.FinalizedPosition.Should().Be(SimulatedBlockchainProvider.SimFinalizedPosition);
        observed.PreparedHash.Should().Be(CanonicalJson.Hash(persisted));
        observed.GroupId.Should().Be(prepared.GroupId);
        (await Balance(provider, Participant, "7")).Should().Be(-10);
        (await Balance(provider, Treasury, "7")).Should().Be(10);
        (await Balance(provider, Reserve, "42")).Should().Be(-400);
        (await Balance(provider, Participant, "42")).Should().Be(400, "a rebroadcast applies the group once");
    }

    [Fact]
    public async Task TamperedOrForeignGroupMovesNeitherLeg()
    {
        var provider = DevelopmentProvider();
        provider.TryGetModule<IExchangeTransactionModule>(out var module).Should().BeTrue();
        var prepared = (await module!.PrepareExchangeAsync(Intent())).Result!;
        var tampered = prepared with { TransactionIds = [.. prepared.TransactionIds.Take(2), SimulatedBlockchainProvider.SimTxPrefix + "other"] };

        (await module.ObserveExchangeAsync(tampered)).IsError.Should().BeTrue();
        await module.Invoking(m => m.SubmitExchangeAsync(tampered)).Should().ThrowAsync<InvalidOperationException>();
        (await module.PrepareExchangeAsync(Intent("algorand:genesis"))).IsError.Should().BeTrue();
        (await module.PrepareExchangeAsync(Intent() with { FeePayerAddress = "REALALGORANDADDRESS" })).IsError.Should().BeTrue();
        (await Balance(provider, Participant, "7")).Should().Be(0);
        (await Balance(provider, Participant, "42")).Should().Be(0);
    }

    [Fact]
    public void WithoutTheActivationPinTheExchangeModuleIsUnadvertisedWhileUngatedModulesRemain()
    {
        var provider = DevelopmentProvider(development: false);

        provider.TryGetModule<IExchangeTransactionModule>(out _).Should().BeFalse();
        provider.TryGetModule<IFungibleCreationModule>(out _).Should().BeTrue();
    }
}
