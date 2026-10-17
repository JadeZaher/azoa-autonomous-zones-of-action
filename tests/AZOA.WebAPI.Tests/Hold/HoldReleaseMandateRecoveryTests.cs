using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Hold;

public sealed class HoldReleaseMandateRecoveryTests
{
    [Theory]
    [InlineData(3, false, "Unresolved")]
    [InlineData(4, false, "Obsolete")]
    [InlineData(4, true, "Applied")]
    public async Task SavedRequestRequiresHigherVersionAndSecondReceiptReadBeforeRetirement(long currentVersion, bool committedDuringRead, string expected)
    {
        var actor = Guid.NewGuid();
        var credential = new SigningCredentialWitness(1, "Jwt", actor, null, null,
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), null, null, [], true,
            TokenUse: AzoaClaims.TokenUseLogin);
        var request = new HoldRecoveryRequest(Guid.NewGuid(), 3, "Withdraw automatic release.");
        var terms = new HoldReleaseMandateTerms(1, "hold", "definition",
            new("Algorand", ChainNetwork.Devnet, "genesis", "42", "reserve"), "1",
            new(Guid.NewGuid(), actor, "recipient", "Algorand"), DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1), "ALGO", 6, new("reserve", "reserve", "1000", "0", "0"));
        var row = new HoldReleaseMandate { Id = "mandate", ActorUserId = actor.ToString("N"), Version = currentVersion,
            Status = "Active", TermsJson = JsonSerializer.Serialize(terms) };
        var applied = new HoldReleaseMandate { Id = row.Id, ActorUserId = row.ActorUserId, Version = 4,
            Status = "Revoked", TermsJson = row.TermsJson };
        var store = new Mock<IHoldReleaseMandateStore>(MockBehavior.Strict);
        store.SetupSequence(value => value.GetRecoveryAsync(row.Id, "MandateRevoke", request, credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync((HoldReleaseMandate?)null).ReturnsAsync(committedDuringRead ? applied : null);
        store.Setup(value => value.GetAsync(row.Id, It.IsAny<CancellationToken>())).ReturnsAsync(row);
        var service = new HoldReleaseMandateService(store.Object, null!, null!, null!, null!, null!);
        var result = await service.ReconcileRecoveryAsync(row.Id, new("MandateRevoke", request), credential);
        result.IsError.Should().BeFalse(); result.Result!.State.Should().Be(expected);
        result.Result.Mandate!.Status.Should().Be(committedDuringRead ? "Revoked" : "Active");
        store.Verify(value => value.GetRecoveryAsync(row.Id, "MandateRevoke", request, credential, It.IsAny<CancellationToken>()),
            Times.Exactly(currentVersion > request.Version ? 2 : 1));
        store.Verify(value => value.GetAsync(row.Id, It.IsAny<CancellationToken>()), Times.Once);
        store.VerifyNoOtherCalls();
    }
}
