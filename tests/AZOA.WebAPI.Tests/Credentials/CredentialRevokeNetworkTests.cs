using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Credentials;

public sealed class CredentialRevokeNetworkTests
{
    [Fact]
    public async Task ReplayRetainsAdmittedNetworkAfterDefaultChanges()
    {
        var rail = new CredentialRailHarness();
        (await rail.IssueAsync("701")).Outcome!.IsError.Should().BeFalse();
        var first = await rail.Manager.RevokeAsync("701", new() { AuthenticationWitness = AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(rail.IssuerA) }, rail.IssuerA, "network-replay");
        first.IsError.Should().BeFalse(first.Message);
        rail.Rail.Factory.SetupGet(factory => factory.DefaultNetwork).Returns(ChainNetwork.Mainnet);

        var replay = await rail.Manager.RevokeAsync("701", new() { AuthenticationWitness = AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(rail.IssuerA) }, rail.IssuerA, "network-replay");

        replay.IsError.Should().BeFalse(replay.Message);
        replay.Result!.LifecycleId.Should().Be(first.Result!.LifecycleId);
        rail.Rail.Factory.Verify(factory => factory.GetProvider(It.IsAny<string>(), ChainNetwork.Mainnet), Times.Never);
        var wrongNetwork = await rail.Manager.RevokeAsync("701", new() { Network = "Mainnet", AuthenticationWitness = AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(rail.IssuerA) }, rail.IssuerA, "network-replay");
        wrongNetwork.Code.Should().Be(AzoaErrorCodes.Conflict);
    }

    [Fact]
    public async Task InitialRevocationCanSelectHistoricalNetwork()
    {
        var rail = new CredentialRailHarness();
        (await rail.IssueAsync("702")).Outcome!.IsError.Should().BeFalse();
        rail.Rail.Factory.SetupGet(factory => factory.DefaultNetwork).Returns(ChainNetwork.Mainnet);

        var result = await rail.Manager.RevokeAsync("702", new CredentialRevokeRequest { Network = "Devnet", AuthenticationWitness = AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(rail.IssuerA) }, rail.IssuerA, "historical");

        result.IsError.Should().BeFalse(result.Message);
        rail.Rail.Factory.Verify(factory => factory.GetProvider(It.IsAny<string>(), ChainNetwork.Mainnet), Times.Never);
    }

    [Fact]
    public async Task CancelledAdmissionNeedsNeitherProviderNorCredentialProjection()
    {
        var rail = new CredentialRailHarness();
        var cancelled = await rail.Rail.Service.CancelRequestAsync(rail.IssuerA, "Revoke", "cancelled", Guid.Empty);
        cancelled.IsError.Should().BeFalse(cancelled.Message);

        var result = await rail.Manager.RevokeAsync("703", new() { AuthenticationWitness = AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(rail.IssuerA) }, rail.IssuerA, "cancelled");

        result.Code.Should().Be("NFT_CANCELLED");
        rail.Rail.Factory.Verify(factory => factory.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()), Times.Never);
    }
}
