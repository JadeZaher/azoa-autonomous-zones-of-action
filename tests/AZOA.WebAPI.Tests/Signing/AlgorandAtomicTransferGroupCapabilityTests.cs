using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Algorand;

namespace AZOA.WebAPI.Tests.Signing;

public sealed class AlgorandAtomicTransferGroupCapabilityTests
{
    [Fact]
    public async Task GroupCapability_IsDiscoveredButFailsClosedWithoutHttpOrCustody()
    {
        using var handler = new CountingHandler();
        var provider = new AlgorandProvider(
            Config(),
            NullLogger<AlgorandProvider>.Instance,
            signerFactory: null,
            keyService: null,
            custodyService: null,
            custodyScopeFactory: null,
            faucet: null,
            httpMessageHandler: handler);
        provider.Initialize(new BlockchainNetworkConfig { NodeUrl = "http://algod.test/" }, ChainNetwork.Devnet);

        var request = AtomicTransferGroupRequest.TryCreate(
            provider,
            "Algorand",
            ChainNetwork.Devnet,
            "settlement-1",
            new AtomicTransferEffect("1", "source", "recipient", 90, SigningContext.Platform),
            new AtomicTransferEffect("1", "source", "treasury", 10, SigningContext.Platform));

        provider.TryGetModule<IAtomicTransferGroupModule>(out var module).Should().BeTrue();
        module.Should().NotBeNull();
        module!.SupportsAtomicTransferGroups.Should().BeFalse();

        var result = await module.SubmitAtomicTransferGroupAsync(request.Result!);

        result.IsError.Should().BeTrue();
        result.Result.Should().BeNull();
        result.Message.Should().Contain("unavailable");
        handler.RequestCount.Should().Be(0, "a disabled group adapter must not submit either leg");
    }

    [Fact]
    public void GroupCapability_RejectsAMismatchedRequestBeforeProviderSubmissionWithoutNetworkIo()
    {
        using var handler = new CountingHandler();
        var provider = new AlgorandProvider(
            Config(), NullLogger<AlgorandProvider>.Instance,
            signerFactory: null, keyService: null, custodyService: null,
            custodyScopeFactory: null, faucet: null, httpMessageHandler: handler);
        provider.Initialize(new BlockchainNetworkConfig { NodeUrl = "http://algod.test/" }, ChainNetwork.Devnet);
        var request = AtomicTransferGroupRequest.TryCreate(
            provider, "Algorand", ChainNetwork.Testnet, "settlement-1",
            new AtomicTransferEffect("1", "source", "recipient", 90, SigningContext.Platform),
            new AtomicTransferEffect("1", "source", "treasury", 10, SigningContext.Platform));

        request.IsError.Should().BeTrue();
        request.Message.Should().Contain("does not match");
        handler.RequestCount.Should().Be(0);
    }

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>())
        .Build();

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
