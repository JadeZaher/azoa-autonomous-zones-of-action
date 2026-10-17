using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Hold;

public sealed class ExchangeHoldSourceRecoveryTests
{
    [Theory]
    [InlineData("Admitted")]
    [InlineData("Committed")]
    [InlineData("Preparing")]
    [InlineData("Prepared")]
    public async Task ReconciliationCannotCreateLiabilityPrepareOrAuthorizeUnsentSource(string state)
    {
        var source = new ExchangeHoldSource { Id = "source", State = state };
        var store = new Mock<IExchangeHoldSourceStore>(MockBehavior.Strict);
        store.Setup(value => value.GetExchangeSourceAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        var provider = new Mock<IBlockchainProviderFactory>(MockBehavior.Strict);
        var service = new ExchangeHoldSourceService(store.Object, provider.Object);
        (await service.ReconcileAsync(source.Id)).Result.Should().BeSameAs(source);
        store.Verify(value => value.GetExchangeSourceAsync(source.Id, It.IsAny<CancellationToken>()), Times.Once);
        store.VerifyNoOtherCalls();
        provider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CrashBeforeSendReplaysOnlySavedSubmittedWireWithoutCustodyOrNewAuthorization()
    {
        var actor = Guid.NewGuid(); var participant = Guid.NewGuid();
        var asset = new HoldAssetIdentity("Algorand", ChainNetwork.Devnet, "genesis", "42", "reserve");
        var opening = new HoldCommandIntent(1, "opening", "Open", "hold", actor, actor, null, actor, asset);
        var intent = new ExchangeHoldSourceIntent(1, "request", Guid.NewGuid(), actor, participant, "{}", opening,
            "7", "10", "project", "treasury", new(Guid.NewGuid(), participant, "sender", "Algorand"), "10000");
        var wireIntent = new ExchangeWireIntent(1, "source", 0, "Algorand", ChainNetwork.Devnet, "genesis", [],
            "reserve", SigningContext.PlatformFor("Algorand"), "10000");
        var wire = new PreparedExchangeWire(wireIntent, "preparation", "group", ["tx"], ["signed"]);
        var source = new ExchangeHoldSource { Id = "source", State = "Submitted", IntentJson = JsonSerializer.Serialize(intent),
            PreparedWireJson = JsonSerializer.Serialize(wire) };
        var store = new Mock<IExchangeHoldSourceStore>(MockBehavior.Strict);
        store.Setup(value => value.GetExchangeSourceAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        var module = new Mock<IExchangeTransactionModule>(MockBehavior.Strict);
        module.Setup(value => value.ObserveExchangeAsync(It.IsAny<PreparedExchangeWire>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ExchangeWireObservation>.Success(new("Unknown", "preparation", "group", "hash", "genesis")));
        module.Setup(value => value.SubmitExchangeAsync(It.Is<PreparedExchangeWire>(saved => JsonSerializer.Serialize(saved) == source.PreparedWireJson),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var provider = new Mock<IBlockchainProvider>();
        provider.Setup(value => value.TryGetModule<IExchangeTransactionModule>(out It.Ref<IExchangeTransactionModule?>.IsAny))
            .Returns((out IExchangeTransactionModule? result) => { result = module.Object; return true; });
        var factory = new Mock<IBlockchainProviderFactory>(MockBehavior.Strict);
        factory.Setup(value => value.GetProvider("Algorand", ChainNetwork.Devnet)).Returns(provider.Object);
        var service = new ExchangeHoldSourceService(store.Object, factory.Object);
        (await service.ReconcileAsync(source.Id)).Result!.State.Should().Be("Submitted");
        module.Verify(value => value.SubmitExchangeAsync(It.IsAny<PreparedExchangeWire>(), It.IsAny<CancellationToken>()), Times.Once);
        module.Verify(value => value.PrepareExchangeAsync(It.IsAny<ExchangeWireIntent>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(value => value.AuthorizeExchangeSubmissionAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
