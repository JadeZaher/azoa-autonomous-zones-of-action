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

public sealed class HoldCommandRequestRecoveryTests
{
    [Theory]
    [InlineData(4, false, "Unresolved")]
    [InlineData(5, false, "Obsolete")]
    [InlineData(5, true, "Applied")]
    public async Task ResolvesConsentOnlyAfterOrderedVersionAndExactReceiptReads(long version, bool applied, string expected)
    {
        var actor = Guid.NewGuid();
        var request = new HoldContinuationRequest(Guid.NewGuid(), 4, "quote", "1000");
        var intent = new HoldCommandIntent(1, "key", "Open", "hold", actor, actor, null, null,
            new("Algorand", ChainNetwork.Testnet, "genesis", "7", "reserve"));
        var row = new HoldCommand { Id = "operation", ActorUserId = actor.ToString("N"), Version = version,
            State = "AwaitingConsent", Kind = "Open", IntentJson = JsonSerializer.Serialize(intent) };
        var receipt = new HoldCommandAuthorization { Id = HoldConsentIdentity.Id("Transfer", row.Id, actor, null, request.RequestId),
            Kind = "Transfer", OperationId = row.Id, ActorUserId = row.ActorUserId,
            RequestHash = HoldConsentIdentity.RequestHash("Transfer", row.Id, actor, null, request) };
        var store = new Mock<IHoldCommandStore>(MockBehavior.Strict);
        var sequence = new MockSequence();
        store.InSequence(sequence).Setup(x => x.GetCommandAsync(row.Id, It.IsAny<CancellationToken>())).ReturnsAsync(row);
        store.InSequence(sequence).Setup(x => x.GetAuthorizationAsync(receipt.Id, It.IsAny<CancellationToken>())).ReturnsAsync(applied ? receipt : null);
        var service = new HoldCommandService(store.Object, null!, null!, null!, null!, null!);

        var result = await service.ReconcileRequestAsync(row.Id, new("Transfer", request), actor, null);

        result.Result!.State.Should().Be(expected);
        store.VerifyAll();
        store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Transfer")]
    [InlineData("Acceptance")]
    public void ExtractedConsentIdentityPreservesExistingCanonicalPropertyNames(string kind)
    {
        var id = "operation"; var actor = Guid.NewGuid(); Guid? tenant = Guid.NewGuid();
        var request = new HoldContinuationRequest(Guid.NewGuid(), 4, "quote", "1000");
        var expectedId = kind == "Transfer"
            ? CanonicalJson.Hash(new { Domain = "hold-continue-v1", id, ActorUserId = actor, TenantId = tenant, request.RequestId })
            : CanonicalJson.Hash(new { Domain = "hold-acceptance-consent-v1", Id = id, ActorUserId = actor, TenantId = tenant, request.RequestId });
        HoldConsentIdentity.Id(kind, id, actor, tenant, request.RequestId).Should().Be(expectedId);
        HoldConsentIdentity.RequestHash(kind, id, actor, tenant, request).Should().Be(kind == "Transfer"
            ? CanonicalJson.Hash(new { id, ActorUserId = actor, TenantId = tenant, request })
            : CanonicalJson.Hash(new { Id = id, ActorUserId = actor, TenantId = tenant, request }));
    }
}
