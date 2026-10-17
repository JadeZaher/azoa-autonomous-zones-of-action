using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using Xunit;
using AZOA.WebAPI.Services.Wormhole;

namespace AZOA.WebAPI.Tests.Services;

/// <summary>Real WTT journeys through the lifecycle seam; see Services/Wormhole/AGENTS.md §Operation journal.</summary>
public sealed partial class WttLifecycleFlowTests
{
    [Fact]
    public async Task TransferSettlesOnlyAfterFinalizedPublicationVerifiedProofAndFinalizedRedemption()
    {
        var rig = new Rig();
        var view = await rig.StartAsync("flow-settle");
        Assert.Equal("Admitted", view.PublicState);

        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        Assert.Equal("SourceSubmitted", view.PublicState);
        Assert.Single(rig.Source.Sent);
        Assert.Equal(1, rig.Source.Custody);

        rig.Guardians.Pending = true;
        rig.Source.Finalize(view.Stages.Single().TransactionId!);
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("AwaitingAttestation", view.PublicState);
        Assert.Equal(rig.Source.LastPublication!.Sequence, view.TransferSequence);
        Assert.False(view.ProofVerified);

        rig.Guardians.Pending = false;
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("AwaitingTargetAuthorization", view.PublicState);
        Assert.True(view.ProofVerified);
        Assert.Empty(rig.Target.Sent);

        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        Assert.Equal("TargetSubmitted", view.PublicState);
        Assert.Single(rig.Target.Sent);
        Assert.Null(view.Settlement);

        var redemption = view.Stages.Last();
        rig.Target.Finalize(redemption.TransactionId!);
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("Settled", view.PublicState);
        Assert.Equal(redemption.TransactionId, view.Settlement!.TargetTransactionId);
        Assert.Equal(rig.Guardians.Vaa!.BodyDigest, view.Settlement.BodyDigest);
        Assert.Equal(view.Intent.Amount.RecipientAmount, view.Settlement.RecipientAmount);
        Assert.Equal(new[] { "Transfer", "Redeem" }, view.Stages.Select(stage => stage.Stage));
    }

    [Fact]
    public async Task LostStartResponseReplaysTheOriginalOperationAndAnotherKeyCannotReuseTheOffer()
    {
        var rig = new Rig();
        var offer = await rig.QuoteAsync("flow-replay");
        rig.Store.LoseNextAdmissionResponse = true;
        await Assert.ThrowsAsync<IOException>(() => rig.Service.StartAsync(rig.Credential, new(offer.OfferId, offer.OfferHash, true), "flow-replay"));
        var replay = Ok(await rig.Service.StartAsync(rig.Credential, new(offer.OfferId, offer.OfferHash, true), "flow-replay"));
        Assert.Equal(offer.OperationId, replay.OperationId);
        Assert.Equal(1, rig.Store.Admissions);
        Assert.True((await rig.Service.StartAsync(rig.Credential, new(offer.OfferId, offer.OfferHash, true), "other-key")).IsError);
        Assert.True((await rig.Service.QuoteAsync(rig.Credential, rig.Request("flow-replay"))).IsError);
        Assert.Equal(replay.OperationId, Ok(await rig.Service.LookupAsync("flow-replay", rig.Credential)).OperationId);
    }

    [Fact]
    public async Task CancellationBeforeFirstSendSealsThePreparedGenerationAndPublishedTransfersCannotBeCancelled()
    {
        var rig = new Rig();
        var offer = await rig.QuoteAsync("flow-cancel");
        var view = Ok(await rig.Service.StartAsync(rig.Credential, new(offer.OfferId, offer.OfferHash, true), "flow-cancel"));
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, false), rig.Credential));
        Assert.Equal("SourcePrepared", view.PublicState);
        Assert.Empty(rig.Source.Sent);
        var prepared = view.Stages.Single();

        view = Ok(await rig.Service.CancelAsync(rig.Credential, new(offer.OfferId, offer.OfferHash), "flow-cancel"));
        Assert.Equal("CancelledBeforeEffect", view.PublicState);
        Assert.Single(rig.Preparations.Sealed);
        Assert.True((await rig.Service.SubmitAsync(view.OperationId, new(view.Version, prepared.StageId, prepared.Version + 1), rig.Credential)).IsError);
        Assert.Empty(rig.Source.Sent);

        var published = new Rig();
        var other = await published.QuoteAsync("flow-published");
        var running = Ok(await published.Service.StartAsync(published.Credential, new(other.OfferId, other.OfferHash, true), "flow-published"));
        running = Ok(await published.Service.ContinueAsync(running.OperationId, new(running.Version, true), published.Credential));
        published.Guardians.Pending = true;
        published.Source.Finalize(running.Stages.Single().TransactionId!);
        Ok(await published.Service.RefreshAsync(running.OperationId, published.Credential));
        var refused = await published.Service.CancelAsync(published.Credential, new(other.OfferId, other.OfferHash), "flow-published");
        Assert.Equal(AzoaErrorCodes.Conflict, refused.Code);
    }

    [Fact]
    public async Task AmbiguousSendReplaysOnlyTheSavedAuthorizedWireWithoutANewSignature()
    {
        var rig = new Rig();
        var view = await rig.StartAsync("flow-ambiguous");
        rig.Source.LoseNextSend = true;
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        Assert.Equal("SourceSubmitted", view.PublicState);
        Assert.Single(rig.Source.Sent);

        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal(2, rig.Source.Sent.Count);
        Assert.Equal(rig.Source.Sent[0], rig.Source.Sent[1]);
        Assert.Equal(1, rig.Source.Custody);
        Assert.Equal("SourceSubmitted", view.PublicState);
    }

    [Fact]
    public async Task BodyOwnedByAnotherOperationParksForOperatorWithoutAnyTargetStage()
    {
        var rig = new Rig();
        var view = await rig.StartAsync("flow-body");
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        rig.Guardians.Pending = true;
        rig.Source.Finalize(view.Stages.Single().TransactionId!);
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        var publication = rig.Source.LastPublication!;
        var deployment = view.Intent.Target.DeploymentIdentity;
        var partition = WttBodyClaimIdentity.Partition(deployment);
        Assert.NotNull(await rig.Claims.ClaimAsync(new WttBodyClaim
        {
            Id = WttBodyClaimIdentity.Id(partition, publication.BodyDigest), TargetPartition = partition, BodyDigest = publication.BodyDigest,
            EmitterChain = publication.EmitterChain, EmitterAddress = publication.EmitterAddress, Sequence = publication.Sequence,
            OperationId = new string('e', 64), BodyHex = publication.BodyHex,
        }));

        rig.Guardians.Pending = false;
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("NeedsOperator", view.PublicState);
        Assert.Single(view.Stages);
        Assert.Empty(rig.Target.Sent);
        Assert.True((await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential)).IsError);
    }

    [Fact]
    public async Task ExternalRecipientOptInIsExposedBeforeDisclosureAndAcceptsOnlyTheExactParticipantSignature()
    {
        var rig = new Rig(participantRecipient: true);
        var view = await rig.PublishAndProveAsync(await rig.StartAsync("flow-participant"));
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        Assert.Equal("AwaitingTargetParticipant", view.PublicState);
        Assert.Equal("SignParticipant", view.NextAction);
        Assert.Equal(0, rig.Target.Custody);
        var stage = view.Stages.Last();
        Assert.Equal("RecipientOptIn", stage.Stage);
        Assert.False(stage.Exposed);

        var command = new WttStageCommand(view.Version, stage.StageId, stage.Version);
        var disclosed = await rig.Service.ExposeParticipantAsync(view.OperationId, command, rig.Credential);
        Assert.False(disclosed.IsError, disclosed.Message);
        Assert.Equal(rig.Recipient, disclosed.Result!.ParticipantAddress);
        var replay = await rig.Service.ExposeParticipantAsync(view.OperationId, command, rig.Credential);
        Assert.Equal(disclosed.Result.UnsignedTransactionBase64, replay.Result!.UnsignedTransactionBase64);

        var forged = await rig.Service.AcceptParticipantAsync(view.OperationId, new(disclosed.Result.OperationVersion, stage.StageId,
            disclosed.Result.StageVersion, "signed:another-message", true), rig.Credential);
        Assert.True(forged.IsError);
        Assert.Empty(rig.Target.Sent);

        view = Ok(await rig.Service.AcceptParticipantAsync(view.OperationId, new(disclosed.Result.OperationVersion, stage.StageId,
            disclosed.Result.StageVersion, "signed:" + disclosed.Result.UnsignedTransactionBase64, true), rig.Credential));
        Assert.Equal("TargetSubmitted", view.PublicState);
        Assert.Equal("signed:" + disclosed.Result.UnsignedTransactionBase64, rig.Target.Sent.Single());
        Assert.Equal(0, rig.Target.Custody);

        rig.Target.Finalize("participant-RecipientOptIn");
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("AwaitingTargetAuthorization", view.PublicState);
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        rig.Target.Finalize(view.Stages.Last().TransactionId!);
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("Settled", view.PublicState);
        Assert.Equal(1, rig.Target.Custody);
    }

    [Fact]
    public async Task FailedUnsignedPreparationRetiresTheGenerationAndRetryUsesABoundedFreshQuote()
    {
        var rig = new Rig();
        var view = await rig.StartAsync("flow-retire");
        rig.Source.FailNextPrepare = true;
        var failed = await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential);
        Assert.True(failed.IsError);
        view = Ok(await rig.Service.GetAsync(view.OperationId, rig.Credential));
        Assert.Equal("Retired", view.Stages.Single().State);
        Assert.Equal("AwaitingSourceAuthorization", view.PublicState);
        Assert.Single(rig.Preparations.Sealed);
        Assert.Empty(rig.Source.Sent);

        var quotes = rig.Source.Quotes;
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        Assert.Equal(quotes + 1, rig.Source.Quotes);
        Assert.Equal(1, view.Stages.Last().Generation);
        Assert.Single(rig.Source.Sent);
    }

    [Fact]
    public async Task RetryAfterAFeeSpendingFailureCountsTheSpentAttemptAgainstTheConsentedBudget()
    {
        var rig = new Rig();
        var view = await rig.StartAsync("flow-budget");
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        rig.Source.FailOnChain(view.Stages.Single().TransactionId!);
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("FailedOnChain", view.Stages.Single().State);
        Assert.Equal("AwaitingSourceAuthorization", view.PublicState);

        rig.Source.NextFee = "900";
        var refused = await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential);
        Assert.True(refused.IsError);
        Assert.Contains("budget", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, rig.Source.Custody);

        rig.Source.NextFee = "800";
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        Assert.Equal(1, view.Stages.Last().Generation);
        Assert.Equal(2, rig.Source.Sent.Count);
    }

    [Fact]
    public async Task NativeOutputNeedsFreshRecipientConsentAndSettlesOnlyAfterItsFinalizedUnwrap()
    {
        var rig = new Rig(unwrapNative: true);
        var view = await rig.PublishAndProveAsync(await rig.StartAsync("flow-native"));
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        rig.Target.Finalize(view.Stages.Last().TransactionId!);
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("StartNativeOutput", view.NextAction);
        Assert.Null(view.Settlement);

        var offer = await rig.Service.QuoteNativeOutputAsync(view.OperationId, rig.Credential);
        Assert.False(offer.IsError, offer.Message);
        var tooLow = await rig.Service.StartNativeOutputAsync(view.OperationId, new(view.Version, "unwrap-1", "4999", offer.Result!.TemporaryRent, true),
            rig.Credential);
        Assert.Equal(AzoaErrorCodes.Conflict, tooLow.Code);

        view = Ok(await rig.Service.StartNativeOutputAsync(view.OperationId, new(view.Version, "unwrap-1", offer.Result.NetworkFee,
            offer.Result.TemporaryRent, true), rig.Credential));
        var stage = view.Stages.Last();
        Assert.Equal("NativeOutput", stage.Phase);
        var disclosed = (await rig.Service.ExposeParticipantAsync(view.OperationId, new(view.Version, stage.StageId, stage.Version), rig.Credential)).Result!;
        view = Ok(await rig.Service.AcceptParticipantAsync(view.OperationId, new(disclosed.OperationVersion, stage.StageId, disclosed.StageVersion,
            "signed:" + disclosed.UnsignedTransactionBase64, true), rig.Credential));
        Assert.Null(view.Settlement);

        rig.Target.Finalize("native-0");
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("Settled", view.PublicState);
        Assert.Equal("native-0", view.Settlement!.NativeOutputTransactionId);
        Assert.Equal("990", view.Settlement.NativeAmountReceived);
    }

    [Fact]
    public async Task ResidualReturnRequiresTheDirectOwnerAndLeavesTheSettledTransferSettled()
    {
        var rig = new Rig();
        var view = await rig.PublishAndProveAsync(await rig.StartAsync("flow-cleanup"));
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        var redemption = view.Stages.Last();
        rig.Target.Finalize(redemption.TransactionId!);
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        var settlement = JsonSerializer.Serialize(view.Settlement);

        var tenantCredential = rig.Credential with { TenantId = Guid.NewGuid(), OwnerAuthority = false, TokenUse = AZOA.WebAPI.Core.AzoaClaims.TokenUseChild };
        var request = new WttCleanupRequest(view.Version, redemption.StageId, "cleanup-1", "5", true);
        Assert.Equal(AzoaErrorCodes.Forbidden, (await rig.Service.StartCleanupAsync(view.OperationId, request, tenantCredential)).Code);

        var offer = await rig.Service.QuoteCleanupAsync(view.OperationId, redemption.StageId, rig.Credential);
        Assert.Equal("500", offer.Result!.ReturnAmount);
        Assert.Equal("400", offer.Result.OriginalAttributedDepositReturn);
        view = Ok(await rig.Service.StartCleanupAsync(view.OperationId, request, rig.Credential));
        Assert.Equal("Settled", view.PublicState);
        Assert.Equal("AwaitFinality", view.NextAction);
        Assert.Equal("cleanup-wire-0", rig.Target.Sent.Last());

        rig.Target.Finalize("cleanup-0");
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("Settled", view.State);
        Assert.Equal(settlement, JsonSerializer.Serialize(view.Settlement));
        Assert.Equal("Confirmed", view.Stages.Last().State);
    }

    [Fact]
    public async Task ConfirmedTransferRecordsItsPublicationAtomicallyAndCancellationIsRefusedEvenFromALegacyReadyJournal()
    {
        var rig = new Rig();
        var offer = await rig.QuoteAsync("flow-atomic");
        var view = Ok(await rig.Service.StartAsync(rig.Credential, new(offer.OfferId, offer.OfferHash, true), "flow-atomic"));
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        var transfer = view.Stages.Single();
        rig.Guardians.Pending = true;
        rig.Source.Finalize(transfer.TransactionId!);
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("AwaitingAttestation", view.PublicState);
        Assert.Equal(rig.Source.LastPublication!.Sequence, view.TransferSequence);

        var cleanup = await rig.Service.StartCleanupAsync(view.OperationId, new(view.Version, transfer.StageId, "cleanup-source", "5", true), rig.Credential);
        Assert.False(cleanup.IsError, cleanup.Message);
        Assert.Equal(AzoaErrorCodes.Conflict,
            (await rig.Service.CancelAsync(rig.Credential, new(offer.OfferId, offer.OfferHash), "flow-atomic")).Code);

        var legacy = new Rig();
        var legacyOffer = await legacy.QuoteAsync("flow-legacy");
        var running = Ok(await legacy.Service.StartAsync(legacy.Credential, new(legacyOffer.OfferId, legacyOffer.OfferHash, true), "flow-legacy"));
        running = Ok(await legacy.Service.ContinueAsync(running.OperationId, new(running.Version, true), legacy.Credential));
        var stage = running.Stages.Single();
        var observation = JsonSerializer.Serialize(new WttStageObservation("Confirmed", stage.TransactionId!, legacy.Source.ProtocolIdentity));
        Assert.NotNull(await legacy.Store.RecordObservationAsync(running.OperationId, running.Version, stage.StageId, stage.Version, observation, "Confirmed"));
        Assert.Equal("Ready", (await legacy.Store.GetAsync(running.OperationId))!.State);
        var refused = await legacy.Service.CancelAsync(legacy.Credential, new(legacyOffer.OfferId, legacyOffer.OfferHash), "flow-legacy");
        Assert.Equal(AzoaErrorCodes.Conflict, refused.Code);
        Assert.NotEqual("Cancelled", (await legacy.Store.GetAsync(running.OperationId))!.State);
    }

    [Theory]
    [InlineData("relayer-fee", AzoaErrorCodes.InvalidRequest)]
    [InlineData("tenant-foreign-recipient", AzoaErrorCodes.Forbidden)]
    [InlineData("tenant-claimed-platform-recipient", AzoaErrorCodes.Forbidden)]
    [InlineData("custodial-recipient-needs-opt-in", AzoaErrorCodes.InvalidRequest)]
    [InlineData("custodial-recipient-native-output", AzoaErrorCodes.InvalidRequest)]
    [InlineData("tenant-own-custodial-recipient", null)]
    [InlineData("direct-foreign-recipient", null)]
    public async Task RecipientsAreLimitedToWhatTheCallerCanProveAndSignAndRelayerFeesAreRefused(string change, string? refusal)
    {
        var rig = new Rig(participantRecipient: change is "relayer-fee" or "tenant-foreign-recipient" or "custodial-recipient-needs-opt-in"
            or "direct-foreign-recipient", unwrapNative: change == "custodial-recipient-native-output");
        var request = rig.Request("flow-recipient-" + change);
        var credential = rig.Credential;
        if (change.StartsWith("tenant-", StringComparison.Ordinal))
            credential = credential with { TenantId = Guid.NewGuid(), OwnerAuthority = false, TokenUse = AZOA.WebAPI.Core.AzoaClaims.TokenUseChild,
                Scopes = [AZOA.WebAPI.Core.AzoaScopes.WttSourceSign, AZOA.WebAPI.Core.AzoaScopes.WttTargetSign] };
        request = change switch
        {
            "relayer-fee" => request with { RelayerFee = "1" },
            "tenant-claimed-platform-recipient" => request with { RecipientAddress = Rig.ClaimedPlatform },
            "custodial-recipient-needs-opt-in" or "tenant-own-custodial-recipient" => request with { RecipientAddress = Rig.OwnCustodial },
            "custodial-recipient-native-output" => request with { RecipientAddress = Rig.TargetPayer },
            _ => request,
        };
        var result = await rig.Service.QuoteAsync(credential, request);
        Assert.Equal(refusal, result.IsError ? result.Code : null);
        if (refusal is null) Assert.Equal(request.RecipientAddress, result.Result!.Intent.RecipientAddress);
        else Assert.Equal(0, rig.Source.Quotes);
    }

    [Fact]
    public async Task ACrashAfterTheNonceReservationKeepsCancellationRefusedUntilReEntrySignsTheSameNonce()
    {
        var rig = new Rig(sourceChain: "Ethereum");
        var offer = await rig.QuoteAsync("flow-evm-crash");
        var view = Ok(await rig.Service.StartAsync(rig.Credential, new(offer.OfferId, offer.OfferHash, true), "flow-evm-crash"));
        rig.Source.CrashAfterReserve = true;
        await Assert.ThrowsAsync<IOException>(() => rig.Service.ContinueAsync(view.OperationId, new(view.Version, false), rig.Credential));
        var stage = (await rig.Store.ReadStagesAsync(view.OperationId)).Single();
        Assert.Equal("Preparing", stage.State);
        Assert.Equal(WttNonceHold.Unsigned, rig.Source.Hold("Source", stage.Stage, stage.Generation));
        var cancel = new WttCancelRequest(offer.OfferId, offer.OfferHash);
        Assert.Equal(AzoaErrorCodes.Conflict, (await rig.Service.CancelAsync(rig.Credential, cancel, "flow-evm-crash")).Code);

        var resumed = (await rig.Store.GetAsync(view.OperationId))!;
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(resumed.Version, false), rig.Credential));
        Assert.Equal("Prepared", view.Stages.Single().State);
        Assert.Equal(1, rig.Source.Reservations);
        Assert.Equal(AzoaErrorCodes.Conflict, (await rig.Service.CancelAsync(rig.Credential, cancel, "flow-evm-crash")).Code);
        Assert.Equal("Active", (await rig.Store.GetAsync(view.OperationId))!.State);
    }

    [Fact]
    public async Task CancellingAnOldPlannedEvmStageSerializesWithItsReservationEpoch()
    {
        var rig = new Rig(sourceChain: "Ethereum");
        var (view, offer, planned) = await PlannedEvmStageAsync(rig, "flow-evm-race");
        // A re-preparation wins the epoch while cancel sits between its reservation probe and its journal write.
        rig.Source.OnNextProbe = async () =>
        {
            var operation = (await rig.Store.GetAsync(view.OperationId))!;
            Assert.NotNull(await rig.Store.BeginPreparationAsync(view.OperationId, operation.Version, SurrealRecordGuid.BareId(planned.Id), planned.Version));
        };
        var cancel = new WttCancelRequest(offer.OfferId, offer.OfferHash);
        Assert.Equal(AzoaErrorCodes.Conflict, (await rig.Service.CancelAsync(rig.Credential, cancel, "flow-evm-race")).Code);
        var resumed = (await rig.Store.GetAsync(view.OperationId))!;
        Assert.Equal("Prepared", Ok(await rig.Service.ContinueAsync(view.OperationId, new(resumed.Version, false), rig.Credential)).Stages.Single().State);
        Assert.Equal(1, rig.Source.Reservations);

        // Cancel wins instead: a preparer that read the old epoch can no longer begin, so no nonce is ever reserved.
        var first = new Rig(sourceChain: "Ethereum");
        var (before, firstOffer, stale) = await PlannedEvmStageAsync(first, "flow-evm-cancel-first");
        Assert.Equal("Cancelled", Ok(await first.Service.CancelAsync(first.Credential, new(firstOffer.OfferId, firstOffer.OfferHash),
            "flow-evm-cancel-first")).State);
        Assert.Null(await first.Store.BeginPreparationAsync(before.OperationId, before.Version, SurrealRecordGuid.BareId(stale.Id), stale.Version));
        var latest = (await first.Store.GetAsync(before.OperationId))!;
        Assert.Equal(AzoaErrorCodes.Conflict, (await first.Service.ContinueAsync(before.OperationId, new(latest.Version, false), first.Credential)).Code);
        Assert.Equal(0, first.Source.Reservations);
    }

    [Theory]
    [InlineData("reserve", "Retired", "Cancelled")]
    [InlineData("sign", "Preparing", null)]
    public async Task AFailedEvmPreparationFreesOnlyAnUnsignedNonceBeforeCancellation(string failAfter, string stageState, string? cancelled)
    {
        var rig = new Rig(sourceChain: "Ethereum");
        var offer = await rig.QuoteAsync("flow-evm-fail-" + failAfter);
        var view = Ok(await rig.Service.StartAsync(rig.Credential, new(offer.OfferId, offer.OfferHash, true), "flow-evm-fail-" + failAfter));
        rig.Source.FailAfter = failAfter;
        Assert.Equal(AzoaErrorCodes.Conflict, (await rig.Service.ContinueAsync(view.OperationId, new(view.Version, false), rig.Credential)).Code);
        var stage = (await rig.Store.ReadStagesAsync(view.OperationId)).Single();
        Assert.Equal(stageState, stage.State);
        Assert.Equal(failAfter == "reserve" ? WttNonceHold.None : WttNonceHold.Held, rig.Source.Hold("Source", stage.Stage, stage.Generation));
        var cancel = await rig.Service.CancelAsync(rig.Credential, new(offer.OfferId, offer.OfferHash), "flow-evm-fail-" + failAfter);
        Assert.Equal(cancelled, cancel.IsError ? null : cancel.Result!.State);
    }

    [Fact]
    public async Task ALegacyUnverifiedEnvelopeIsReplacedByTheVerifiedOneWithItsFirstProof()
    {
        var rig = new Rig();
        var view = await rig.StartAsync("flow-legacy-envelope");
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        rig.Guardians.Pending = true;
        rig.Source.Finalize(view.Stages.Single().TransactionId!);
        Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        var parked = (await rig.Store.GetAsync(view.OperationId))!;
        var legacy = Convert.ToBase64String(new byte[80]);
        Assert.NotNull(await rig.Store.UpdateAsync(view.OperationId, parked.Version, new(TransferEnvelopeBase64: legacy)));

        rig.Guardians.Pending = false;
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("AwaitingTargetAuthorization", view.PublicState);
        var proven = (await rig.Store.GetAsync(view.OperationId))!;
        Assert.NotEqual(legacy, proven.TransferEnvelopeBase64);
        Assert.Equal(rig.Source.LastPublication!.BodyDigest, WttLifecycleService.Read<WttTargetProofs>(proven.ProofsJson!).Transfer.Vaa.BodyDigest);
    }

    [Fact]
    public async Task AnOfferPastItsExpiryOnTheInjectedClockIsNeverAdmitted()
    {
        var rig = new Rig();
        var offer = await rig.QuoteAsync("flow-expired");
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(AzoaErrorCodes.Conflict, (await rig.Service.StartAsync(rig.Credential, new(offer.OfferId, offer.OfferHash, true), "flow-expired")).Code);
        Assert.Equal(0, rig.Store.Admissions);
    }

    /// <summary>Claims an EVM source stage, then stops the process before its reservation epoch, leaving it Planned.</summary>
    private static async Task<(WttOperationView View, WttOfferView Offer, WttOperationStage Planned)> PlannedEvmStageAsync(Rig rig, string key)
    {
        var offer = await rig.QuoteAsync(key);
        var view = Ok(await rig.Service.StartAsync(rig.Credential, new(offer.OfferId, offer.OfferHash, true), key));
        rig.Store.FailNextPreparationEpoch = true;
        await Assert.ThrowsAsync<IOException>(() => rig.Service.ContinueAsync(view.OperationId, new(view.Version, false), rig.Credential));
        var planned = (await rig.Store.ReadStagesAsync(view.OperationId)).Single();
        Assert.Equal("Planned", planned.State);
        var current = (await rig.Store.GetAsync(view.OperationId))!;
        return (view with { Version = current.Version }, offer, planned);
    }

    [Fact]
    public async Task AnotherTenantScopeCannotReadOrRefreshTheUsersDirectTransfer()
    {
        var rig = new Rig();
        var view = await rig.StartAsync("flow-isolation");
        var tenant = rig.Credential with { TenantId = Guid.NewGuid(), OwnerAuthority = false, TokenUse = AZOA.WebAPI.Core.AzoaClaims.TokenUseChild,
            Scopes = [AZOA.WebAPI.Core.AzoaScopes.WttSourceSign] };
        Assert.Equal(AzoaErrorCodes.NotFound, (await rig.Service.GetAsync(view.OperationId, tenant)).Code);
        Assert.Equal(AzoaErrorCodes.NotFound, (await rig.Service.RefreshAsync(view.OperationId, tenant)).Code);
        Assert.Empty((await rig.Service.ListAsync(tenant, null, 20)).Result!.Items);
        var unscoped = rig.Credential with { OwnerAuthority = false, Scopes = ["swap:sign"] };
        Assert.Equal(AzoaErrorCodes.Forbidden, (await rig.Service.GetAsync(view.OperationId, unscoped)).Code);
        Assert.Single((await rig.Service.ListAsync(rig.Credential, null, 20)).Result!.Items);
        Assert.Equal(AzoaErrorCodes.InvalidRequest, (await rig.Service.ListAsync(rig.Credential, "3155378976000000000", 20)).Code);
    }

    [Fact]
    public async Task AnUnverifiableGuardianEnvelopeIsNeverPersistedAndAVerifiedRefetchKeepsTheSameBody()
    {
        var rig = new Rig();
        var view = await rig.StartAsync("flow-guardian");
        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(view.Version, true), rig.Credential));
        rig.Guardians.WrongSigner = true;
        rig.Source.Finalize(view.Stages.Single().TransactionId!);
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("AwaitingAttestation", view.PublicState);
        Assert.Null((await rig.Store.GetAsync(view.OperationId))!.TransferEnvelopeBase64);
        Assert.Contains("guardian", view.Detail, StringComparison.OrdinalIgnoreCase);

        rig.Guardians.WrongSigner = false;
        view = Ok(await rig.Service.RefreshAsync(view.OperationId, rig.Credential));
        Assert.Equal("AwaitingTargetAuthorization", view.PublicState);
        var recorded = (await rig.Store.GetAsync(view.OperationId))!;
        var digest = WttLifecycleService.Read<WttTargetProofs>(recorded.ProofsJson!).Transfer.Vaa.BodyDigest;

        rig.Guardians.SignatureFill = 2;
        Assert.True(await rig.Recovery.RefetchProofAsync(view.OperationId));
        var replaced = (await rig.Store.GetAsync(view.OperationId))!;
        Assert.NotEqual(recorded.TransferEnvelopeBase64, replaced.TransferEnvelopeBase64);
        Assert.Equal(digest, WttLifecycleService.Read<WttTargetProofs>(replaced.ProofsJson!).Transfer.Vaa.BodyDigest);

        view = Ok(await rig.Service.ContinueAsync(view.OperationId, new(replaced.Version, true), rig.Credential));
        Assert.Equal("TargetSubmitted", view.PublicState);
        rig.Guardians.SignatureFill = 3;
        Assert.False(await rig.Recovery.RefetchProofAsync(view.OperationId));
    }
}
