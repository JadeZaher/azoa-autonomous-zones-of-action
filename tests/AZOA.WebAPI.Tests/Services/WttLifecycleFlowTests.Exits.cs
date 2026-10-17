using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;
using AZOA.WebAPI.Tests.TestSupport;
using Microsoft.Extensions.Options;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

/// <summary>Round-4 exits: exposed-participant absence, owner takeover, publication re-attribution, recipient directory.</summary>
public sealed partial class WttLifecycleFlowTests
{
    [Fact]
    public async Task AnExposedOptInThatExpiresUnsignedResolvesWithoutEffectAndContinueIssuesAFreshGeneration()
    {
        var rig = new Rig(participantRecipient: true);
        var view = await rig.PublishAndProveAsync(await rig.StartAsync("flow-exposed-expiry"));
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        var stage = view.Stages.Last();
        Assert.False((await rig.Service.ExposeParticipantAsync(view.OperationId, new(view.Version, stage.StageId, stage.Version), rig.Credential)).IsError);

        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal(("Exposed", "AwaitingTargetParticipant"), (view.Stages.Last().State, view.PublicState));

        rig.Target.ExposedVerdict = "ExpiredNoEffect";
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("ExpiredNoEffect", view.Stages.Last().State);
        Assert.Equal("AwaitingTargetAuthorization", view.PublicState);
        Assert.Empty(rig.Target.Sent);

        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        var fresh = view.Stages.Last();
        Assert.Equal(("RecipientOptIn", 1, false), (fresh.Stage, fresh.Generation, fresh.Exposed));
    }

    [Fact]
    public async Task AParticipantWhoBroadcastsTheirOwnSignatureIsRecognisedAndSettlesWithoutServerAuthorization()
    {
        var rig = new Rig(participantRecipient: true);
        var view = await rig.PublishAndProveAsync(await rig.StartAsync("flow-exposed-landed"));
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        var stage = view.Stages.Last();
        var disclosed = await rig.Service.ExposeParticipantAsync(view.OperationId, new(view.Version, stage.StageId, stage.Version), rig.Credential);

        rig.Target.ExposedVerdict = "Landed";
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("SubmissionAuthorized", view.Stages.Last().State);
        // Only the participant's own exact signed message is ever replayed; custody never signed for the recipient.
        Assert.Equal(["signed:" + disclosed.Result!.UnsignedTransactionBase64], rig.Target.Sent.Distinct());
        Assert.Equal(0, rig.Target.Custody);

        rig.Target.Finalize("participant-RecipientOptIn");
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("Confirmed", view.Stages.Last().State);
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        rig.Target.Finalize(view.Stages.Last().TransactionId!);
        Assert.Equal("Settled", Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential)).PublicState);
    }

    [Fact]
    public async Task TheOwnerTakesOverATenantStartedTargetPhaseAfterRevocationAndTheTenantIsLockedOut()
    {
        var rig = new Rig();
        var tenant = rig.TenantCredential(Guid.NewGuid());
        var offer = Ok(await rig.Service.QuoteAsync(tenant, rig.Request("flow-takeover")));
        var view = Ok(await rig.Service.StartAsync(tenant, new(offer.OfferId, offer.OfferHash, true), "flow-takeover"));
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), tenant));
        rig.Source.Finalize(view.Stages.Last().TransactionId!);
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, tenant));
        Assert.Equal("AwaitingTargetAuthorization", view.PublicState);

        rig.GrantRevoked = true;
        Assert.Equal(AzoaErrorCodes.Forbidden, (await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), tenant)).Code);
        Assert.Equal(AzoaErrorCodes.Forbidden,
            (await rig.Service.TakeOverAsync(view.OperationId, new(view.Version), tenant)).Code);

        view = Ok(await rig.Service.GetAsync(view.OperationId, rig.Credential));
        view = Ok(await rig.Service.TakeOverAsync(view.OperationId, new(view.Version), rig.Credential));
        rig.GrantRevoked = false;
        Assert.Equal(AzoaErrorCodes.Forbidden, (await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), tenant)).Code);

        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        rig.Target.Finalize(view.Stages.Last().TransactionId!);
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("Settled", view.PublicState);
        Assert.Equal(rig.Recipient, view.Settlement!.RecipientAddress);
        // The projection keeps the consented intent; only signing authority moved to the owner.
        Assert.Equal(tenant.TenantId, view.Intent.TenantId);
    }

    [Fact]
    public async Task OwnerTakeoverIsRefusedOnceATargetStageWasSentUnderTheTenant()
    {
        var rig = new Rig();
        var tenant = rig.TenantCredential(Guid.NewGuid());
        var offer = Ok(await rig.Service.QuoteAsync(tenant, rig.Request("flow-takeover-sent")));
        var view = Ok(await rig.Service.StartAsync(tenant, new(offer.OfferId, offer.OfferHash, true), "flow-takeover-sent"));
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), tenant));
        rig.Source.Finalize(view.Stages.Last().TransactionId!);
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, tenant));
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), tenant));
        Assert.Equal("TargetSubmitted", view.PublicState);

        var refused = await rig.Service.TakeOverAsync(view.OperationId, new(view.Version), rig.Credential);
        Assert.Equal(AzoaErrorCodes.Conflict, refused.Code);
        Assert.Null((await rig.Store.GetAsync(view.OperationId))!.OwnerIntentJson);
    }

    [Fact]
    public async Task AConfirmedTransferParkedWithoutItsPublicationResumesOnlyWithAnExactlyAttributedPublication()
    {
        var rig = new Rig();
        var offer = await rig.QuoteAsync("flow-reattribute");
        var view = Ok(await rig.Service.StartAsync(rig.Credential, new(offer.OfferId, offer.OfferHash, true), "flow-reattribute"));
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        var stage = view.Stages.Single();
        // A journal written before atomic completion: Confirmed with no attributed publication, then parked by recovery.
        var legacy = JsonSerializer.Serialize(new WttStageObservation("Confirmed", stage.TransactionId!, rig.Source.ProtocolIdentity));
        Assert.NotNull(await rig.Store.RecordObservationAsync(view.OperationId, view.Version, stage.StageId, stage.Version, legacy, "Confirmed"));
        await rig.Recovery.ReconcileAsync(view.OperationId);
        Assert.Equal("NeedsOperator", (await rig.Store.GetAsync(view.OperationId))!.State);

        rig.Source.Finalize(stage.TransactionId!);
        rig.Source.DriftPublicationNonce = true;
        Assert.False(await rig.Recovery.ResumeProofAsync(view.OperationId));
        Assert.Null((await rig.Store.GetAsync(view.OperationId))!.TransferPublicationJson);

        rig.Source.DriftPublicationNonce = false;
        Assert.True(await rig.Recovery.ResumeProofAsync(view.OperationId));
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal(rig.Source.LastPublication!.Sequence, view.TransferSequence);
        Assert.Equal("AwaitingTargetAuthorization", view.PublicState);
    }

    [Fact]
    public async Task ADriftedPublicationParksTheTransferAndAnAttributedReobservationResumesIt()
    {
        var rig = new Rig();
        var view = await rig.StartAsync("flow-drift");
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        rig.Source.Finalize(view.Stages.Single().TransactionId!);
        rig.Source.DriftPublicationNonce = true;
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("NeedsOperator", view.State);
        Assert.Null(view.TransferSequence);

        rig.Source.DriftPublicationNonce = false;
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("AwaitingTargetAuthorization", view.PublicState);
    }

    [Theory]
    [InlineData("tenant-proven-external-with-opt-in", true, false, AzoaErrorCodes.InvalidRequest)]
    [InlineData("tenant-proven-external", false, false, null)]
    [InlineData("tenant-unproven-external", false, false, AzoaErrorCodes.Forbidden)]
    [InlineData("direct-recipient-is-another-users-custody", true, true, AzoaErrorCodes.InvalidRequest)]
    [InlineData("direct-recipient-signs-for-itself", true, false, null)]
    public async Task RecipientsAndParticipantStepsMatchWhoCanActuallySign(string change, bool optIn, bool anotherUsersCustody, string? refusal)
    {
        var rig = new Rig(participantRecipient: optIn);
        var service = rig.ServiceWith(recipients: new RecipientDirectory(anotherUsersCustody),
            ownership: new SignatureProof(change != "tenant-unproven-external"));
        var tenant = change.StartsWith("tenant-", StringComparison.Ordinal);
        var request = rig.Request("flow-recipient-proof-" + change);
        if (tenant) request = request with { RecipientAddress = Rig.ExternalLinked };
        var result = await service.QuoteAsync(tenant ? rig.TenantCredential(Guid.NewGuid()) : rig.Credential, request);
        Assert.Equal(refusal, result.IsError ? result.Code : null);
        Assert.Equal(refusal is null ? 1 : 0, rig.Source.Quotes);
    }

    [Fact]
    public async Task StageKeyRoutesRefuseNewTransfersUntilTheKeyringChecksReadyWhileOtherRoutesStillAdmit()
    {
        var state = new WttStageKeyReadinessState();
        var solana = new Rig(stageKeys: true);
        var gated = solana.ServiceWith(state);
        Assert.Equal(AzoaErrorCodes.DependencyUnavailable, (await gated.QuoteAsync(solana.Credential, solana.Request("flow-keys-unchecked"))).Code);

        using var unconfigured = new WttStageKeyProtector(Options.Create(new WttStageKeyOptions()));
        var degraded = await new WttStageKeyReadiness(new InMemoryWttStageKeyStore(), unconfigured, state).CheckAsync();
        Assert.Equal(AzoaErrorCodes.DependencyUnavailable, (await gated.QuoteAsync(solana.Credential, solana.Request("flow-keys-degraded"))).Code);
        var plain = new Rig();
        Assert.False((await plain.ServiceWith(state).QuoteAsync(plain.Credential, plain.Request("flow-keys-plain"))).IsError);

        using var configured = new WttStageKeyProtector(Options.Create(new WttStageKeyOptions
            { ActiveKeyId = "v1", Keys = new() { ["v1"] = Convert.ToBase64String(new byte[32]) } }));
        Assert.True((await new WttStageKeyReadiness(new InMemoryWttStageKeyStore(), configured, state).CheckAsync()).Ready);
        Assert.False((await gated.QuoteAsync(solana.Credential, solana.Request("flow-keys-ready"))).IsError);
        Assert.False(degraded.Ready);
    }

    private sealed class RecipientDirectory(bool anotherUsersCustody) : IWttRecipientDirectory
    {
        public Task<bool> IsAnotherUsersCustodialWalletAsync(Guid actor, string chainType, string address, CancellationToken ct = default)
            => Task.FromResult(anotherUsersCustody);
    }

    /// <summary>The wallet ownership challenge's persisted verdict for every External wallet.</summary>
    private sealed class SignatureProof(bool proven) : IWalletOwnershipProof
    {
        public Task<bool> IsSignatureProvenAsync(Guid walletId, Guid ownerUserId, CancellationToken ct = default) => Task.FromResult(proven);
    }

    private static T Ok<T>(AZOAResult<T> result)
    {
        Assert.False(result.IsError, result.Message);
        return result.Result!;
    }
}
