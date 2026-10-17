using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Fiat;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AZOA.WebAPI.Tests.Fiat;

public sealed class FiatEventRecoveryTests
{
    [Fact]
    public async Task ReplayUsesDurableCorrelationAndPreservesHandlerFailure()
    {
        var intent = Guid.NewGuid();
        var row = new FiatEvent { Id = "event", Environment = "test", Status = FiatEventStatuses.Received,
            EventType = "payout.paid", IntentId = $"hosted_session:{intent:N}", ProviderAccountId = "acct_stored", ProviderObjectId = "po_stored" };
        var handler = new Mock<IFiatEventHandler>();
        handler.SetupGet(x => x.Name).Returns("test");
        handler.Setup(x => x.Handles(row.EventType)).Returns(true);
        handler.Setup(x => x.ApplyAsync(It.Is<FiatEventContext>(c => c.IntentId == intent
                && c.ProviderAccountId == "acct_stored" && c.ProviderObjectId == "po_stored"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FiatEventOutcome.Failed);
        var inbox = new Mock<IStripeWebhookInbox>();
        inbox.Setup(x => x.RecordOutcomeAsync(row.Id, FiatEventStatuses.Failed, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<FiatEvent>.Success(row));
        (await FiatEventDispatcher.ApplyAsync(row, RailEnvironment.Test, inbox.Object, [handler.Object], NullLogger.Instance, default)).IsError.Should().BeFalse();
        handler.VerifyAll();
        inbox.VerifyAll();
    }

    [Fact]
    public async Task OppositeModeNeverInvokesAHandler()
    {
        var row = new FiatEvent { Environment = "live", Livemode = true, Status = FiatEventStatuses.Received };
        var handler = new Mock<IFiatEventHandler>(MockBehavior.Strict);
        var result = await FiatEventDispatcher.ApplyAsync(row, RailEnvironment.Test,
            Mock.Of<IStripeWebhookInbox>(), [handler.Object], NullLogger.Instance, default);
        result.IsError.Should().BeTrue();
        handler.VerifyNoOtherCalls();
    }
}
