using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Users;

public sealed class NftStageConsentTests
{
    [Fact]
    public async Task ProvenAbsentParticipantAttemptRequiresExplicitConsentForNewGeneration()
    {
        var (h, _, recipient, id) = await Issued();
        var original = await h.Acceptance(id);
        h.Confirm = false;
        await h.Service.RefreshAsync(recipient, id, true, credential: NftLifecycleHarness.Credential(recipient), acceptance: original);
        h.ObservationOverride = "ExpiredAbsent";
        var observed = await h.Service.ReconcileAsync(id);
        observed.Result!.State.Should().Be("Preparing");
        observed.Result.PreparationGeneration.Should().Be(1);
        observed.Result.NeedsStageAcceptance.Should().BeTrue();
        observed.Result.StageAcceptance.Should().BeNull();
        var count = h.Prepared.Count;
        await h.Service.RefreshAsync(recipient, id, true, credential: NftLifecycleHarness.Credential(recipient), acceptance: original);
        h.Prepared.Should().HaveCount(count);
        h.ObservationOverride = null;
        h.Confirm = true;
        var replacement = await h.Acceptance(id);
        await h.Service.RefreshAsync(recipient, id, true, credential: NftLifecycleHarness.Credential(recipient), acceptance: replacement);
        h.Prepared.Should().HaveCount(count + 1);
        var history = NftStageConsentIdentity.Read((await h.Store.GetAsync(id))!);
        history.Should().HaveCount(2);
        history[0].Request.Should().Be(original);
        history[1].Request.Should().Be(replacement);
    }

    [Fact]
    public async Task OriginalAcceptanceResumesAfterCrashBeforeCustodyWithoutAcceptingLaterDelivery()
    {
        var (h, _, recipient, id) = await Issued();
        var request = await h.Acceptance(id);
        h.BeforePrepare = _ => throw new HttpRequestException("Crash before custody");
        await Assert.ThrowsAsync<HttpRequestException>(() => h.Service.RefreshAsync(recipient, id, true,
            credential: NftLifecycleHarness.Credential(recipient), acceptance: request));
        var saved = (await h.Store.GetAsync(id))!;
        saved.State.Should().Be("Preparing");
        NftStageConsentIdentity.Current(saved)!.Request.Should().Be(request);
        var visible = await h.Service.GetAsync(recipient, id);
        visible.Result!.StageAcceptance.Should().Be(request);
        h.BeforePrepare = null;
        var resumed = await h.Service.RefreshAsync(recipient, id, true,
            credential: NftLifecycleHarness.Credential(recipient), acceptance: request);
        resumed.Result!.Stage.Should().Be("Deliver");
        var count = h.Prepared.Count;
        await h.Service.RefreshAsync(recipient, id, true,
            credential: NftLifecycleHarness.Credential(recipient), acceptance: request);
        h.Prepared.Should().HaveCount(count);
        (await h.Store.GetAsync(id))!.StageConsentsJson.Should().Be(saved.StageConsentsJson);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("stage")]
    [InlineData("generation")]
    public async Task DelayedAcceptanceCannotAuthorizeAnotherStage(string change)
    {
        var (h, _, recipient, id) = await Issued();
        var request = await h.Acceptance(id);
        request = change switch
        {
            "version" => request with { ExpectedVersion = request.ExpectedVersion + 1 },
            "stage" => request with { Stage = "Deliver" },
            _ => request with { Generation = request.Generation + 1 },
        };
        var count = h.Prepared.Count;
        var result = await h.Service.RefreshAsync(recipient, id, true,
            credential: NftLifecycleHarness.Credential(recipient), acceptance: request);
        result.IsError.Should().BeTrue();
        (await h.Store.GetAsync(id))!.StageConsentsJson.Should().Be("[]");
        h.Prepared.Should().HaveCount(count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecipientAcceptanceNeedsActualMatchingCredential(bool wrongActor)
    {
        var (h, actor, recipient, id) = await Issued();
        var before = (await h.Store.GetAsync(id))!;
        var count = h.Prepared.Count;
        var result = await h.Service.RefreshAsync(recipient, id, true,
            credential: wrongActor ? NftLifecycleHarness.Credential(actor) : null, acceptance: await h.Acceptance(id));

        result.IsError.Should().BeTrue();
        (await h.Store.GetAsync(id))!.Should().BeEquivalentTo(before);
        h.Prepared.Should().HaveCount(count);
    }

    [Fact]
    public async Task SavedParticipantConsentSurvivesLostResponseWithoutReplacingOriginalActor()
    {
        var (h, actor, recipient, id) = await Issued();
        var original = (await h.Store.GetAsync(id))!.RequestJson;
        h.LoseSubmitResponse = true;
        var request = await h.Acceptance(id);
        var accepted = await h.Service.RefreshAsync(recipient, id, true, credential: NftLifecycleHarness.Credential(recipient), acceptance: request);
        accepted.Result!.State.Should().Be("Submitted");
        var row = (await h.Store.GetAsync(id))!;
        var saved = NftStageConsentIdentity.Current(row)!;
        saved.Authority.Credential.ActorUserId.Should().Be(recipient);
        JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!.ActorAuthority!.Credential.ActorUserId.Should().Be(actor);
        var count = h.Prepared.Count;

        await h.Service.RefreshAsync(recipient, id, true, credential: NftLifecycleHarness.Credential(recipient), acceptance: request);

        var replay = (await h.Store.GetAsync(id))!;
        replay.RequestJson.Should().Be(original);
        replay.StageConsentsJson.Should().Be(row.StageConsentsJson);
        h.Prepared.Should().HaveCount(count);
    }

    [Fact]
    public async Task StageConsentCannotBeReusedForDifferentGenerationWalletOrPhysicalNetwork()
    {
        var (h, _, recipient, id) = await Issued();
        h.Confirm = false;
        await h.Service.RefreshAsync(recipient, id, true, credential: NftLifecycleHarness.Credential(recipient), acceptance: await h.Acceptance(id));
        var row = (await h.Store.GetAsync(id))!;
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!;
        var consent = NftStageConsentIdentity.Current(row)!;
        NftStageConsentIdentity.Matches(row, command, consent).Should().BeTrue();
        NftStageConsentIdentity.Matches(row, command with { RecipientWalletId = Guid.NewGuid() }, consent).Should().BeFalse();
        row.PreparationGeneration++;
        NftStageConsentIdentity.Matches(row, command, consent).Should().BeFalse();
        row.PreparationGeneration--;
        row.ProtocolIdentity = "other-genesis";
        NftStageConsentIdentity.Matches(row, command, consent).Should().BeFalse();
    }

    [Fact]
    public async Task ConsentAppendRejectsSubmittedStageAndRewrittenHistory()
    {
        var (h, _, recipient, id) = await Issued();
        var before = (await h.Store.GetAsync(id))!;
        h.Confirm = false;
        await h.Service.RefreshAsync(recipient, id, true, credential: NftLifecycleHarness.Credential(recipient), acceptance: await h.Acceptance(id));
        var after = (await h.Store.GetAsync(id))!;
        after.State = "Preparing";
        after.PreparedTransactionJson = null;
        NftStageConsentIdentity.CanAppend(before, after).Should().BeTrue();
        after.ProtocolIdentity = "different-chain";
        NftStageConsentIdentity.CanAppend(before, after).Should().BeFalse();
        after.ProtocolIdentity = before.ProtocolIdentity;
        before.State = "Submitted";
        NftStageConsentIdentity.CanAppend(before, after).Should().BeFalse();
        before.State = "AwaitingRecipientAcceptance";
        before.StageConsentsJson = after.StageConsentsJson;
        var old = NftStageConsentIdentity.Read(after).Single();
        after.StageConsentsJson = JsonSerializer.Serialize(new[] { old with { IntentHash = new string('a', 64) },
            old with { Generation = old.Generation + 1 } });
        NftStageConsentIdentity.CanAppend(before, after).Should().BeFalse();
    }

    private static async Task<(NftLifecycleHarness Harness, Guid Actor, Guid Recipient, string Id)> Issued()
    {
        var h = new NftLifecycleHarness();
        var actor = Guid.NewGuid(); var recipient = Guid.NewGuid();
        h.AddWallet(actor); h.AddWallet(recipient);
        var request = NftLifecycleHarness.MintRequest(false); request.IsSoulbound = true;
        var result = await h.Service.MintAsync(actor, recipient, request, "stage-consent",
            credential: NftLifecycleHarness.Credential(actor));
        result.Result!.State.Should().Be("AwaitingRecipientAcceptance");
        return (h, actor, recipient, result.Result.OperationId);
    }
}
