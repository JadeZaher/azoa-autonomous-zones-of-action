using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Hold;

public sealed class HoldWorkflowOpeningRecoveryTests
{
    [Theory]
    [InlineData(3, false, "Unresolved")]
    [InlineData(4, false, "Obsolete")]
    [InlineData(4, true, "Applied")]
    public async Task ReadsVersionBeforeReceiptSoOnlyFencedRequestsCanBeRetired(long version, bool applied, string state)
    {
        var actor = Guid.NewGuid();
        var asset = new HoldAssetIdentity("Algorand", ChainNetwork.Devnet, "genesis", "1", "reserve");
        var intent = new HoldCommandIntent(1, "key", "Open", "hold", actor, actor, null, null, asset);
        var request = new HoldWorkflowOpeningAcceptance(Guid.NewGuid(), 3, new string('a', 64));
        var now = DateTimeOffset.UtcNow;
        var terms = new HoldReleaseMandateTerms(1, "hold", new string('b', 64), asset, "1",
            new(Guid.NewGuid(), actor, "recipient", "Algorand"), now, now.AddDays(1), "ALGO", 6,
            new("reserve", "reserve", "1000", "0", "0"));
        var quote = new ValueCustodyQuote(1, "quote", new("Algorand", ChainNetwork.Devnet, "1", "reserve", "recipient", "1", "reserve"),
            "genesis", "mechanics", "ALGO", 6, "1000", true, "0", "0", now, now.AddMinutes(5), terms.MaximumCosts);
        var draft = new HoldWorkflowMandateDraft(1, Guid.NewGuid().ToString("N"), terms, quote);
        var row = new HoldCommand { Id = "operation", ActorUserId = actor.ToString("N"), Version = version,
            State = "AwaitingConsent", Kind = "Open", IntentHash = CanonicalJson.Hash(intent),
            IntentJson = JsonSerializer.Serialize(intent), WorkflowMandateDraftJson = JsonSerializer.Serialize(draft) };
        var audit = new HoldRecoveryReceipt { Id = HoldWorkflowOpeningIdentity.AuditId(row.Id, actor, null, request.RequestId),
            Kind = "WorkflowOpeningAccept", OperationId = row.Id, ActorUserId = row.ActorUserId,
            RequestHash = HoldWorkflowOpeningIdentity.RequestHash(row.Id, row.IntentHash, request) };
        var sequence = new MockSequence();
        var store = new Mock<IHoldCommandStore>(MockBehavior.Strict);
        store.InSequence(sequence).Setup(value => value.GetCommandAsync(row.Id, It.IsAny<CancellationToken>())).ReturnsAsync(row);
        store.InSequence(sequence).Setup(value => value.GetRecoveryAsync(audit.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(applied ? audit : null);
        var service = new HoldCommandService(store.Object, null!, null!, null!, null!, null!);
        var result = await service.ReconcileWorkflowOpeningAsync(row.Id, request, actor, null);
        result.IsError.Should().BeFalse();
        result.Result!.State.Should().Be(state);
        store.Verify(value => value.GetCommandAsync(row.Id, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(value => value.GetRecoveryAsync(audit.Id, It.IsAny<CancellationToken>()), Times.Once);
        store.VerifyNoOtherCalls();
    }
}
