using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Simulated;
using AZOA.WebAPI.Services.Custody;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AZOA.WebAPI.Tests.Providers;

/// <summary>Issuance's recipient-preparation step on Blockchain:Mode=Simulated, through the real preparer.</summary>
public sealed class SimulatedRecipientPreparationTests
{
    [Theory]
    [InlineData("Simulated")]
    [InlineData("Algorand")] // Simulated mode answers for every chain type, as the dev node's ARDA rail does.
    public async Task AFreshCustodialRecipientIsAlreadyAbleToReceiveOnTheSimulatedRail(string chainType)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Blockchain:Mode"] = "Simulated" })
            .Build();
        BlockchainProviderRegistration[] registrations =
            [new("Simulated", () => new SimulatedBlockchainProvider(config, NullLogger<SimulatedBlockchainProvider>.Instance))];
        var factory = new BlockchainProviderFactory(registrations, config);
        var preparer = new CustodialRecipientPreparer(factory, NullLogger<CustodialRecipientPreparer>.Instance);

        var result = await preparer.PrepareAsync(new CustodialRecipientPreparation(
            chainType, "7737444992541383711", "sim:simulated:recipient",
            SigningContext.ForUser(Guid.NewGuid(), Guid.NewGuid()).ForChain(chainType),
            "sim:simulated:reserve", SigningContext.PlatformFor(chainType)));

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.AlreadyPrepared.Should().BeTrue();
        result.Result.FundedBaseUnits.Should().Be("0");
    }
}
