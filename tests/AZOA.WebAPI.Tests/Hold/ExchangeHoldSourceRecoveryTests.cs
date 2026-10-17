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

/// <summary>The worker path observes, rebroadcasts or abandons; it never claims custody or authorizes a first send.</summary>
public sealed class ExchangeHoldSourceRecoveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Admitted", 120, false)]
    [InlineData("Committed", 5, false)]
    [InlineData("Committed", 120, true)]
    [InlineData("Preparing", 120, true)]
    [InlineData("Prepared", 120, true)]
    public async Task ReconciliationAbandonsOnlyStaleNeverSentCommitmentsAndNeverPreparesOrAuthorizes(string state, int idleMinutes,
        bool abandons)
    {
        var source = new ExchangeHoldSource { Id = "source", State = state, UpdatedAt = Now.AddMinutes(-idleMinutes) };
        var store = new Mock<IExchangeHoldSourceStore>(MockBehavior.Strict);
        store.Setup(value => value.GetExchangeSourceAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        store.Setup(value => value.AbandonUnsentExchangeAsync(source.Id, Now.AddHours(-1), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExchangeHoldSettlementReceipt { Id = source.Id });
        var service = new ExchangeHoldSourceService(store.Object, new Mock<IBlockchainProviderFactory>(MockBehavior.Strict).Object,
            new ExchangeOptions { UnsentSourceAbandonAfter = TimeSpan.FromHours(1) }, new FixedClock(Now));

        (await service.ReconcileAsync(source.Id)).Result.Should().BeSameAs(source);

        store.Verify(value => value.AbandonUnsentExchangeAsync(source.Id, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            abandons ? Times.Once() : Times.Never());
        store.Verify(value => value.BeginExchangePreparationAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(value => value.AuthorizeExchangeSubmissionAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(value => value.CommitExchangeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubmittedWireIsOnlyRebroadcastAndALostSubmitResponseStaysPending(bool submitFails)
    {
        var (source, module, factory) = Submitted();
        if (submitFails)
            module.Setup(value => value.SubmitExchangeAsync(It.IsAny<PreparedExchangeWire>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("connection reset"));
        var store = new Mock<IExchangeHoldSourceStore>(MockBehavior.Strict);
        store.Setup(value => value.GetExchangeSourceAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        var service = new ExchangeHoldSourceService(store.Object, factory.Object, new ExchangeOptions(), new FixedClock(Now));

        var result = await service.ReconcileAsync(source.Id);

        result.IsError.Should().BeFalse("an unanswered rebroadcast leaves the authorized bytes for the next pass");
        result.Result!.State.Should().Be("Submitted");
        module.Verify(value => value.SubmitExchangeAsync(It.Is<PreparedExchangeWire>(saved => JsonSerializer.Serialize(saved) == source.PreparedWireJson),
            It.IsAny<CancellationToken>()), Times.Once);
        module.Verify(value => value.PrepareExchangeAsync(It.IsAny<ExchangeWireIntent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static (ExchangeHoldSource Source, Mock<IExchangeTransactionModule> Module, Mock<IBlockchainProviderFactory> Factory) Submitted()
    {
        var actor = Guid.NewGuid(); var participant = Guid.NewGuid();
        var asset = new HoldAssetIdentity("Algorand", ChainNetwork.Devnet, "genesis", "42", "reserve");
        var intent = new ExchangeHoldSourceIntent(1, "request", Guid.NewGuid(), actor, participant, "{}", null,
            "7", "10", "project", "treasury", new(Guid.NewGuid(), participant, "sender", "Algorand"), "10000",
            Units: new("Issue", asset, "400", "contributors"));
        var wireIntent = new ExchangeWireIntent(1, "source", 0, "Algorand", ChainNetwork.Devnet, "genesis", [],
            "reserve", SigningContext.PlatformFor("Algorand"), "10000");
        var wire = new PreparedExchangeWire(wireIntent, "preparation", "group", ["tx"], ["signed"]);
        var source = new ExchangeHoldSource { Id = "source", State = "Submitted", IntentJson = JsonSerializer.Serialize(intent),
            PreparedWireJson = JsonSerializer.Serialize(wire), UpdatedAt = Now };
        var module = new Mock<IExchangeTransactionModule>(MockBehavior.Strict);
        module.Setup(value => value.ObserveExchangeAsync(It.IsAny<PreparedExchangeWire>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ExchangeWireObservation>.Success(new("Unknown", "preparation", "group", "hash", "genesis")));
        module.Setup(value => value.SubmitExchangeAsync(It.IsAny<PreparedExchangeWire>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var provider = new Mock<IBlockchainProvider>();
        provider.Setup(value => value.TryGetModule<IExchangeTransactionModule>(out It.Ref<IExchangeTransactionModule?>.IsAny))
            .Returns((out IExchangeTransactionModule? result) => { result = module.Object; return true; });
        var factory = new Mock<IBlockchainProviderFactory>(MockBehavior.Strict);
        factory.Setup(value => value.GetProvider("Algorand", ChainNetwork.Devnet)).Returns(provider.Object);
        return (source, module, factory);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
