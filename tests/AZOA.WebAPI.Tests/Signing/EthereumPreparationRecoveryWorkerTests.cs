using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using AZOA.WebAPI.Services.Ethereum;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Signing;

public sealed class EthereumPreparationRecoveryWorkerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_enabled_network_is_replayed_even_when_first_network_fails(bool firstFails)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Blockchain:Chains:0:ChainType"] = "Ethereum",
            ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
            ["Blockchain:Chains:0:Testnet:IsEnabled"] = "true",
            ["Blockchain:Chains:0:Mainnet:IsEnabled"] = "false",
        }).Build();
        var first = new Mock<IBlockchainProvider>(MockBehavior.Strict);
        var firstRecovery = first.As<IEthereumRecoveryModule>();
        firstRecovery.Setup(module => module.ReplayAuthorizedAsync(It.IsAny<CancellationToken>()))
            .Returns(firstFails ? Task.FromException(new HttpRequestException("network outage")) : Task.CompletedTask);
        var second = new Mock<IBlockchainProvider>(MockBehavior.Strict);
        var secondRecovery = second.As<IEthereumRecoveryModule>();
        secondRecovery.Setup(module => module.ReplayAuthorizedAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var factory = new Mock<IBlockchainProviderFactory>(MockBehavior.Strict);
        factory.Setup(item => item.GetProvider("Ethereum", ChainNetwork.Devnet)).Returns(first.Object);
        factory.Setup(item => item.GetProvider("Ethereum", ChainNetwork.Testnet)).Returns(second.Object);
        using var services = new ServiceCollection().AddSingleton(factory.Object).BuildServiceProvider();
        var logger = new Mock<ILogger<EthereumPreparationRecoveryWorker>>();
        using var worker = new EthereumPreparationRecoveryWorker(services.GetRequiredService<IServiceScopeFactory>(), config, logger.Object);
        await worker.SweepAsync(default);
        firstRecovery.Verify(module => module.ReplayAuthorizedAsync(It.IsAny<CancellationToken>()), Times.Once);
        secondRecovery.Verify(module => module.ReplayAuthorizedAsync(It.IsAny<CancellationToken>()), Times.Once);
        factory.Verify(item => item.GetProvider("Ethereum", ChainNetwork.Mainnet), Times.Never);
        factory.Verify(item => item.GetAllEnabledProviders(), Times.Never);
        logger.Invocations.Count(call => call.Method.Name == "Log").Should().Be(firstFails ? 1 : 0);
    }
}
