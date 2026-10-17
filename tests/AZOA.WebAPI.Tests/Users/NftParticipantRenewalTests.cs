using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Users;

/// <summary>A lapsed participant stage credential is renewed only by that participant, for the same stage and generation.</summary>
public sealed class NftParticipantRenewalTests
{
    [Fact]
    public async Task LapsedRecipientConsentBeforePreparationIsRenewedByTheRecipientAndPreparationResumes()
    {
        var h = new NftLifecycleHarness();
        var issuer = Guid.NewGuid(); var recipient = Guid.NewGuid();
        h.AddWallet(issuer); h.AddWallet(recipient);
        var request = NftLifecycleHarness.MintRequest(false); request.IsSoulbound = true;
        var id = (await h.Service.MintAsync(issuer, recipient, request, "participant-lapse", credential: NftLifecycleHarness.Credential(issuer))).Result!.OperationId;
        var original = await h.Acceptance(id);
        h.BeforePrepare = _ => throw new HttpRequestException("Crash before custody");
        await Assert.ThrowsAsync<HttpRequestException>(() => h.Service.RefreshAsync(recipient, id, true,
            credential: NftLifecycleHarness.Credential(recipient), acceptance: original));
        h.BeforePrepare = null;
        (await h.Service.RefreshAsync(recipient, id, true, credential: NftLifecycleHarness.Credential(recipient), acceptance: await h.Acceptance(id)))
            .IsError.Should().BeTrue("a live participant credential cannot be superseded to bump the version");
        AZOA.WebAPI.Helpers.NftStageConsentIdentity.Read((await h.Store.GetAsync(id))!).Should().ContainSingle();
        h.Clock.Offset = TimeSpan.FromHours(25);
        var signatures = h.Prepared.Count;
        var stalled = (await h.Service.GetAsync(recipient, id)).Result!;
        stalled.NeedsParticipantRenewal.Should().BeTrue();
        stalled.RenewalParticipantUserId.Should().Be(recipient);
        var requester = new NftStageAcceptance(Guid.NewGuid(), stalled.Version, stalled.State, stalled.Stage, stalled.PreparationGeneration);
        (await h.Service.RenewActorAuthorityAsync(id, requester, h.FreshCredential(issuer))).IsError.Should().BeFalse();

        (await h.Service.RefreshAsync(recipient, id, true, credential: h.FreshCredential(recipient), acceptance: original))
            .IsError.Should().BeTrue("replaying the original acceptance still fences its lapsed credential");
        var issuerAttempt = await h.Acceptance(id);
        (await h.Service.RefreshAsync(issuer, id, true, credential: h.FreshCredential(issuer), acceptance: issuerAttempt))
            .IsError.Should().BeTrue("the requester cannot renew the recipient's acceptance");
        h.Prepared.Should().HaveCount(signatures);
        var renewal = await h.Acceptance(id);
        var resumed = await h.Service.RefreshAsync(recipient, id, true, credential: h.FreshCredential(recipient), acceptance: renewal);

        resumed.Result!.Stage.Should().Be("Deliver");
        h.Prepared.Should().HaveCount(signatures + 1);
        var history = AZOA.WebAPI.Helpers.NftStageConsentIdentity.Read((await h.Store.GetAsync(id))!);
        history.Should().HaveCount(2);
        history[0].Request.Should().Be(original);
        history[1].Request.Should().Be(renewal);
        history[1].IntentHash.Should().Be(history[0].IntentHash, "renewal re-affirms the identical stage intent");
    }
}
