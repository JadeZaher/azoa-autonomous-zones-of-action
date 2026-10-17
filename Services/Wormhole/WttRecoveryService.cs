using System.Text.Json;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Wormhole;

/// <summary>Observes and replays only authorized saved wire, then advances proof and settlement; never signs or quotes a new effect.</summary>
public sealed class WttRecoveryService(IWttOperationStore store, WttModuleResolver modules, WttGuardianTransport guardians,
    WttProofVerifier verifier, IWttBodyClaimStore claims, ILogger<WttRecoveryService> logger)
{
    private static readonly string[] TerminalVerdicts = ["Confirmed", "FailedOnChain", "ExpiredNoEffect"];

    public async Task ReconcileAsync(string operationId, CancellationToken ct = default)
    {
        for (var pass = 0; pass < 6; pass++)
        {
            ct.ThrowIfCancellationRequested();
            var operation = await store.GetAsync(operationId, ct);
            if (operation is null) return;
            var progressed = operation.State switch
            {
                "Active" or "NeedsOperator" => await ObserveCurrentAsync(operationId, operation, ct),
                "Ready" => await AdvanceReadyAsync(operationId, operation, ct),
                "AwaitingProof" => await AdvanceProofAsync(operationId, operation, ct),
                _ => false,
            };
            if (!progressed) return;
        }
    }

    /// <summary>Operator retry after a parked proof or publication; re-observes and re-verifies only, never signs.</summary>
    public async Task<bool> ResumeProofAsync(string operationId, CancellationToken ct = default)
    {
        var operation = await store.GetAsync(operationId, ct);
        if (operation is { State: "NeedsOperator", Phase: "Source", TransferPublicationJson: null, CurrentStageId: not null })
            return await ReattributePublicationAsync(operationId, operation, ct);
        if (operation is not { State: "NeedsOperator", ProofsJson: null, TransferPublicationJson: not null, CurrentStageId: { } stageId })
            return false;
        var stage = await store.GetStageAsync(stageId, ct);
        if (stage is null || !WttStageStates.IsTerminal(stage.State)) return false;
        return await store.UpdateAsync(operationId, operation.Version, new(State: "AwaitingProof",
            Detail: "Operator resumed guardian proof verification.", NextAttemptAt: DateTimeOffset.UtcNow), ct) is not null;
    }

    /// <summary>Re-observes a confirmed source Transfer parked without its attributed publication (publication-mismatch exit).</summary>
    private async Task<bool> ReattributePublicationAsync(string operationId, WttOperation operation, CancellationToken ct)
    {
        var stages = await store.ReadStagesAsync(operationId, ct);
        var current = stages.LastOrDefault(stage => SurrealRecordGuid.BareId(stage.Id) == operation.CurrentStageId);
        var transfer = stages.LastOrDefault(stage => stage.Phase == "Source" && stage.Stage == "Transfer" && stage.State == "Confirmed");
        if (current is null || !WttStageStates.IsTerminal(current.State) || transfer?.PreparedJson is null) return false;
        var intent = WttLifecycleService.Intent(operation);
        var prepared = WttLifecycleService.Read<WttStagePreparation>(transfer.PreparedJson);
        var observed = await ObserveAsync(operation, transfer, prepared, ct);
        if (observed is not { Observation: { Verdict: "Confirmed" } observation } || observation.TransactionId != prepared.TransactionId
            || observation.ProtocolIdentity != prepared.ProtocolIdentity)
            return false;
        var completion = await CompletionAsync(operationId, intent, observation, ct);
        if (completion is null) return false;
        return await store.UpdateAsync(operationId, operation.Version, new(State: "AwaitingProof",
            TransferPublicationJson: completion.TransferPublicationJson, AttestationPublicationJson: completion.AttestationPublicationJson,
            Detail: "Operator re-observed the confirmed transfer; its exact attributed publication is recorded.",
            NextAttemptAt: DateTimeOffset.UtcNow), ct) is not null;
    }

    private async Task<bool> ObserveCurrentAsync(string operationId, WttOperation operation, CancellationToken ct)
    {
        if (operation.CurrentStageId is not { } stageId) return false;
        var stage = await store.GetStageAsync(stageId, ct);
        if (stage is { State: "Exposed", AuthorizedAt: null, ParticipantJson: not null } && operation.State == "Active")
            return await ObserveExposedAsync(operationId, operation, stage, ct);
        if (stage is null || stage.AuthorizedAt is null || stage.PreparedJson is null || stage.State is not ("SubmissionAuthorized" or "NeedsOperator"))
            return false;
        var intent = WttLifecycleService.Intent(operation);
        var prepared = WttLifecycleService.Read<WttStagePreparation>(stage.PreparedJson);
        var observed = await ObserveAsync(operation, stage, prepared, ct);
        if (observed is null) return false;
        var (observation, json) = observed.Value;
        var verdict = observation.TransactionId != prepared.TransactionId || observation.ProtocolIdentity != prepared.ProtocolIdentity
            ? "NeedsOperator"
            : TerminalVerdicts.Contains(observation.Verdict) || observation.Verdict == "NeedsOperator" ? observation.Verdict : "Pending";
        WttSourceCompletion? completion = null;
        if (verdict == "Confirmed" && stage.Phase == "Source" && stage.Stage == "Transfer")
        {
            completion = await CompletionAsync(operationId, intent, observation, ct);
            if (completion is null) verdict = "NeedsOperator";
        }
        var recorded = await store.RecordObservationAsync(operationId, operation.Version, stageId, stage.Version, json, verdict, completion, ct);
        if (recorded is null) return false;
        if (verdict == "Pending" && recorded.State == "SubmissionAuthorized")
        {
            await SubmitAsync(operation, recorded, prepared, ct);
            return false;
        }
        return TerminalVerdicts.Contains(verdict);
    }

    /// <summary>Resolves a disclosed, never-authorized participant message from provider evidence: landed, expired unsigned, or scan progress.</summary>
    private async Task<bool> ObserveExposedAsync(string operationId, WttOperation operation, WttOperationStage stage, CancellationToken ct)
    {
        var intent = StageIntent(operation, stage);
        var participant = WttLifecycleService.Read<WttParticipantPreparation>(stage.ParticipantJson!);
        var previous = WttLifecycleService.Observation(stage)?.EvidenceJson;
        AZOAResult<WttExposedObservation> observed;
        if (stage.Phase == "NativeOutput")
        {
            var module = modules.Resolve<IWttNativeOutputModule>(intent.Target.ChainType, intent.Target.Network);
            if (module is null || stage.ConsentJson is null) return false;
            observed = await module.ObserveExposedWttNativeOutputAsync(intent, WttLifecycleService.Read<WttNativeOutputQuote>(stage.QuoteJson),
                WttLifecycleService.Read<WttNativeOutputConsent>(stage.ConsentJson), participant, previous, ct);
        }
        else
        {
            var module = modules.Resolve<IWttParticipantModule>(intent.Target.ChainType, intent.Target.Network);
            if (module is null) return false;
            observed = await module.ObserveExposedWttParticipantAsync(intent, WttLifecycleService.Read<WttStageQuote>(stage.QuoteJson),
                participant, previous, ct);
        }
        if (observed.IsError || observed.Result is not { } result)
        {
            logger.LogInformation("WTT exposed participant {Stage} for {OperationId} is unobservable: {Reason}", stage.Stage, operationId, observed.Message);
            return false;
        }
        if (result.Verdict == "Landed" && (result.Landed is not { } landed || landed.Stage != stage.Stage || landed.Generation != stage.Generation
                || landed.Reservation != participant.Reservation))
            return false;
        var evidence = new WttStageObservation(result.Verdict, result.Landed?.TransactionId ?? "", participant.ProtocolIdentity,
            result.EvidenceJson, Detail: result.Detail);
        var json = stage.Phase == "NativeOutput" ? Canonical(new WttNativeOutputObservation(evidence, null, null)) : Canonical(evidence);
        var resolved = await store.ResolveExposedAsync(operationId, operation.Version, SurrealRecordGuid.BareId(stage.Id), stage.Version, json,
            result.Verdict, result.Landed is { } signed ? Canonical(signed) : null, ct);
        return resolved is not null && result.Verdict is "Landed" or "ExpiredNoEffect";
    }

    /// <summary>The exact intent a stage's saved quote is bound to; stages quoted before an owner takeover keep the admitted intent.</summary>
    private static WttOperationIntent StageIntent(WttOperation operation, WttOperationStage stage)
        => WttLifecycleService.IntentFor(operation, stage.Phase switch
        {
            "NativeOutput" => WttLifecycleService.Read<WttNativeOutputQuote>(stage.QuoteJson).IntentHash,
            "Cleanup" => WttLifecycleService.Read<WttCleanupQuote>(stage.QuoteJson).IntentHash,
            _ => WttLifecycleService.Read<WttStageQuote>(stage.QuoteJson).IntentHash,
        });

    private async Task<bool> AdvanceReadyAsync(string operationId, WttOperation operation, CancellationToken ct)
    {
        var intent = WttLifecycleService.Intent(operation);
        var stages = await store.ReadStagesAsync(operationId, ct);
        switch (operation.Phase)
        {
            case "Source":
            {
                var confirmed = stages.Where(stage => stage.Phase == "Source" && stage.State == "Confirmed").ToArray();
                var transfer = confirmed.LastOrDefault(stage => stage.Stage == "Transfer");
                var attest = confirmed.LastOrDefault(stage => stage.Stage == "Attest");
                if (transfer is null || intent.RequiresAttestation && attest is null) return false;
                var transferPublication = Attributed(WttLifecycleService.Observation(transfer)?.Publication, intent);
                var attestPublication = intent.RequiresAttestation ? Attributed(WttLifecycleService.Observation(attest!)?.Publication, intent) : null;
                if (transferPublication is null || intent.RequiresAttestation && attestPublication is null)
                    return await ParkAsync(operationId, operation, "A confirmed source stage lacks its exact attributed core publication.", ct);
                return await store.UpdateAsync(operationId, operation.Version, new(State: "AwaitingProof",
                    TransferPublicationJson: Canonical(transferPublication),
                    AttestationPublicationJson: attestPublication is null ? null : Canonical(attestPublication),
                    Detail: "Source publication finalized; awaiting guardian signatures.", NextAttemptAt: DateTimeOffset.UtcNow), ct) is not null;
            }
            case "Target":
            {
                var redeem = stages.LastOrDefault(stage => stage.Phase == "Target" && stage.Stage == "Redeem" && stage.State == "Confirmed");
                if (redeem?.PreparedJson is null || WttLifecycleService.Observation(redeem) is not { } observation) return false;
                if (intent.UnwrapNative && intent.Target.ChainType == "Solana")
                {
                    await store.UpdateAsync(operationId, operation.Version, new(Phase: "NativeOutput",
                        Detail: "Tokens redeemed to the recipient token account; native output awaits the recipient's explicit consent."), ct);
                    return false;
                }
                return await SettleAsync(operationId, operation, intent, redeem, observation, null, ct);
            }
            case "NativeOutput":
            {
                var output = stages.LastOrDefault(stage => stage.Phase == "NativeOutput" && stage.State == "Confirmed");
                var redeem = stages.LastOrDefault(stage => stage.Phase == "Target" && stage.Stage == "Redeem" && stage.State == "Confirmed");
                if (output?.ObservationJson is null || redeem is null || WttLifecycleService.Observation(redeem) is not { } observation) return false;
                return await SettleAsync(operationId, operation, intent, redeem, observation,
                    WttLifecycleService.Read<WttNativeOutputObservation>(output.ObservationJson), ct);
            }
            default:
                return false;
        }
    }

    private async Task<bool> AdvanceProofAsync(string operationId, WttOperation operation, CancellationToken ct)
    {
        var intent = WttLifecycleService.Intent(operation);
        if (operation.TransferPublicationJson is null) return false;
        var transfer = WttLifecycleService.Read<WttPublication>(operation.TransferPublicationJson);
        var attestation = operation.AttestationPublicationJson is { } stored ? WttLifecycleService.Read<WttPublication>(stored) : null;
        var targetState = modules.Resolve<IWttGuardianStateSource>(intent.Target.ChainType, intent.Target.Network);
        if (targetState is null) return false;
        // Envelopes are persisted only with their verified proof, so a bad or expired-set response never wedges the journal.
        var transferProof = await FetchVerifiedAsync(intent, transfer, operation.TransferEnvelopeBase64, targetState, ct);
        var attestationProof = attestation is null ? NoProof
            : await FetchVerifiedAsync(intent, attestation, operation.AttestationEnvelopeBase64, targetState, ct);
        if (transferProof.Proof is null || attestation is not null && attestationProof.Proof is null)
        {
            var reason = transferProof.Proof is null ? transferProof.Detail : attestationProof.Detail;
            await NoteAsync(operationId, operation, "Awaiting a verifiable guardian proof: " + reason, ct);
            return false;
        }
        try
        {
            _ = WttProofBinding.Transfer(intent, transferProof.Proof);
            if (attestationProof.Proof is { } attested) _ = WttProofBinding.Attestation(intent, attested);
        }
        catch (InvalidDataException exception) { return await ParkAsync(operationId, operation, exception.Message, ct); }
        var vaa = transferProof.Proof.Vaa;
        var deployment = intent.Target.DeploymentIdentity;
        var partition = WttBodyClaimIdentity.Partition(deployment);
        var owned = await claims.ClaimAsync(new WttBodyClaim
        {
            Id = WttBodyClaimIdentity.Id(partition, vaa.BodyDigest), TargetPartition = partition,
            TargetProtocolChain = deployment.ProtocolChain, TargetGenesisHex = deployment.GenesisHex,
            TargetCoreBridgeHex = deployment.CoreBridgeHex, TargetTokenBridgeHex = deployment.TokenBridgeHex,
            TargetProtocolIdentity = intent.Target.ProtocolIdentity, TargetCoreBridge = intent.Target.CoreBridge,
            TargetTokenBridge = intent.Target.TokenBridge, SourceProtocolIdentity = intent.Source.ProtocolIdentity,
            EmitterChain = vaa.EmitterChain, EmitterAddress = vaa.EmitterAddress, Sequence = vaa.Sequence,
            BodyDigest = vaa.BodyDigest, BodyHex = vaa.BodyHex, OperationId = operationId, IntentHash = operation.IntentHash,
            ActorUserId = operation.ActorUserId, TenantId = operation.TenantId, CreatedAt = DateTimeOffset.UtcNow,
        }, ct);
        if (owned is null)
            return await ParkAsync(operationId, operation, "The canonical body or its emitter sequence is owned by another operation.", ct);
        await store.UpdateAsync(operationId, operation.Version, new(State: "Ready", Phase: "Target",
            TransferEnvelopeBase64: transferProof.Envelope, AttestationEnvelopeBase64: attestationProof.Envelope,
            ProofsJson: Canonical(new WttTargetProofs(transferProof.Proof, attestationProof.Proof)),
            BodyClaimId: SurrealRecordGuid.BareId(owned.Id),
            Detail: "Guardian proof verified and body claimed; target stages await the target payer's authorization."), ct);
        return false;
    }

    private async Task<bool> SettleAsync(string operationId, WttOperation operation, WttOperationIntent intent, WttOperationStage redeem,
        WttStageObservation observation, WttNativeOutputObservation? native, CancellationToken ct)
    {
        if (operation.ProofsJson is null) return false;
        var proofs = WttLifecycleService.Read<WttTargetProofs>(operation.ProofsJson);
        var prepared = WttLifecycleService.Read<WttStagePreparation>(redeem.PreparedJson!);
        var settlement = new WttSettlement(1, prepared.TransactionId, intent.Target.ProtocolIdentity, proofs.Transfer.Vaa.BodyDigest,
            intent.RecipientAddress, intent.Amount.RecipientAmount, observation.EvidenceJson ?? "{}",
            native?.Stage.TransactionId, native?.NativeAmountReceived);
        return await store.UpdateAsync(operationId, operation.Version, new(State: "Settled", Phase: "Complete",
            SettlementJson: Canonical(settlement), Detail: "Finalized target redemption settled this transfer."), ct) is not null;
    }

    /// <summary>Operator re-fetch after the recorded signer set became unusable; the body must be identical and no target attempt sent.</summary>
    public async Task<bool> RefetchProofAsync(string operationId, CancellationToken ct = default)
    {
        var operation = await store.GetAsync(operationId, ct);
        if (operation is not { Phase: "Target", State: "Ready" or "NeedsOperator", ProofsJson: { } recorded, TransferPublicationJson: { } transferJson })
            return false;
        var intent = WttLifecycleService.Intent(operation);
        var previous = WttLifecycleService.Read<WttTargetProofs>(recorded);
        var targetState = modules.Resolve<IWttGuardianStateSource>(intent.Target.ChainType, intent.Target.Network);
        if (targetState is null) return false;
        var transfer = await FetchVerifiedAsync(intent, WttLifecycleService.Read<WttPublication>(transferJson), null, targetState, ct);
        var attestation = operation.AttestationPublicationJson is { } attestationJson
            ? await FetchVerifiedAsync(intent, WttLifecycleService.Read<WttPublication>(attestationJson), null, targetState, ct) : NoProof;
        if (transfer.Proof is null || transfer.Envelope is null || transfer.Proof.Vaa.BodyDigest != previous.Transfer.Vaa.BodyDigest
            || (previous.Attestation is null) != (attestation.Proof is null)
            || attestation.Proof is not null && attestation.Proof.Vaa.BodyDigest != previous.Attestation!.Vaa.BodyDigest)
            return false;
        return await store.ReplaceProofAsync(operationId, operation.Version, recorded, transfer.Envelope, attestation.Envelope,
            Canonical(new WttTargetProofs(transfer.Proof, attestation.Proof)), ct) is not null;
    }

    private static readonly (WttVerifiedProof? Proof, string? Envelope, string Detail) NoProof = (null, null, "");

    private async Task<(WttVerifiedProof? Proof, string? Envelope, string Detail)> FetchVerifiedAsync(WttOperationIntent intent,
        WttPublication publication, string? persisted, IWttGuardianStateSource targetState, CancellationToken ct)
    {
        var expected = new WttProofExpectation(intent.Target.ProtocolIdentity, intent.Target.CoreBridge, intent.Target.TokenBridge,
            publication.BodyHex);
        if (persisted is not null)
        {
            // A legacy unverified envelope is retried once, then replaced by a fresh fetch instead of wedging the journal.
            var reused = await verifier.VerifyAsync(persisted, expected, targetState, ct);
            if (!reused.IsError && reused.Result is not null) return (reused.Result, persisted, "verified");
        }
        var fetched = await guardians.FetchAsync(publication.EmitterChain, publication.EmitterAddress, publication.Sequence,
            publication.BodyHex, ct);
        if (!fetched.Found || fetched.EnvelopeBase64 is null) return (null, null, fetched.Detail);
        var verified = await verifier.VerifyAsync(fetched.EnvelopeBase64, expected, targetState, ct);
        return verified.IsError || verified.Result is null ? (null, null, verified.Message) : (verified.Result, fetched.EnvelopeBase64, "verified");
    }

    private async Task NoteAsync(string operationId, WttOperation operation, string detail, CancellationToken ct)
    {
        var bounded = detail[..Math.Min(detail.Length, 512)];
        logger.LogInformation("WTT proof for {OperationId}: {Detail}", operationId, bounded);
        if (operation.Detail != bounded) await store.UpdateAsync(operationId, operation.Version, new(Detail: bounded), ct);
    }

    private async Task<WttSourceCompletion?> CompletionAsync(string operationId, WttOperationIntent intent, WttStageObservation observation,
        CancellationToken ct)
    {
        var transfer = Attributed(observation.Publication, intent);
        if (transfer is null) return null;
        string? attestationJson = null;
        if (intent.RequiresAttestation)
        {
            var attest = (await store.ReadStagesAsync(operationId, ct))
                .LastOrDefault(stage => stage.Phase == "Source" && stage.Stage == "Attest" && stage.State == "Confirmed");
            if (attest is null || Attributed(WttLifecycleService.Observation(attest)?.Publication, intent) is not { } attestation) return null;
            attestationJson = Canonical(attestation);
        }
        return new(Canonical(transfer), attestationJson);
    }

    private async Task<bool> ParkAsync(string operationId, WttOperation operation, string reason, CancellationToken ct)
    {
        logger.LogWarning("WTT operation {OperationId} needs operator reconciliation: {Reason}", operationId, reason);
        await store.UpdateAsync(operationId, operation.Version, new(State: "NeedsOperator", Detail: reason), ct);
        return false;
    }

    private static WttPublication? Attributed(WttPublication? publication, WttOperationIntent intent)
    {
        return publication is not null && publication.EmitterChain == intent.Source.ProtocolChain
            && publication.EmitterAddress == intent.Source.EmitterAddressHex && publication.Nonce == intent.Nonce
            && publication.ConsistencyLevel == intent.ConsistencyLevel && publication.ProtocolIdentity == intent.Source.ProtocolIdentity
            ? publication : null;
    }

    private async Task<(WttStageObservation Observation, string Json)?> ObserveAsync(WttOperation operation,
        WttOperationStage stage, WttStagePreparation prepared, CancellationToken ct)
    {
        var intent = StageIntent(operation, stage);
        switch (stage.Phase)
        {
            case "Source":
            {
                var module = modules.Resolve<IWttSourceModule>(intent.Source.ChainType, intent.Source.Network);
                if (module is null) return null;
                var result = await module.ObserveWttSourceAsync(intent, WttLifecycleService.Read<WttStageQuote>(stage.QuoteJson), prepared, ct);
                return result.IsError || result.Result is null ? Unobserved(stage, result.Message) : (result.Result, Canonical(result.Result));
            }
            case "Target":
            {
                var module = modules.Resolve<IWttTargetModule>(intent.Target.ChainType, intent.Target.Network);
                if (module is null || operation.ProofsJson is null) return null;
                var result = await module.ObserveWttTargetAsync(intent, WttLifecycleService.Read<WttTargetProofs>(operation.ProofsJson),
                    WttLifecycleService.Read<WttStageQuote>(stage.QuoteJson), prepared, ct);
                return result.IsError || result.Result is null ? Unobserved(stage, result.Message) : (result.Result, Canonical(result.Result));
            }
            case "NativeOutput":
            {
                var module = modules.Resolve<IWttNativeOutputModule>(intent.Target.ChainType, intent.Target.Network);
                if (module is null || stage.ConsentJson is null) return null;
                var result = await module.ObserveWttNativeOutputAsync(intent, WttLifecycleService.Read<WttNativeOutputQuote>(stage.QuoteJson),
                    WttLifecycleService.Read<WttNativeOutputConsent>(stage.ConsentJson), prepared, ct);
                return result.IsError || result.Result is null ? Unobserved(stage, result.Message) : (result.Result.Stage, Canonical(result.Result));
            }
            case "Cleanup":
            {
                var network = await CleanupNetworkAsync(intent, stage, ct);
                var module = network is null ? null : modules.Resolve<IWttCleanupModule>(network.ChainType, network.Network);
                if (module is null || stage.ConsentJson is null) return null;
                var result = await module.ObserveWttCleanupAsync(intent, WttLifecycleService.Read<WttCleanupQuote>(stage.QuoteJson),
                    WttLifecycleService.Read<WttCleanupConsent>(stage.ConsentJson), prepared, ct);
                return result.IsError || result.Result is null ? Unobserved(stage, result.Message) : (result.Result.Stage, Canonical(result.Result));
            }
            default:
                return null;
        }
    }

    private async Task SubmitAsync(WttOperation operation, WttOperationStage stage, WttStagePreparation prepared, CancellationToken ct)
    {
        var intent = StageIntent(operation, stage);
        try
        {
            switch (stage.Phase)
            {
                case "Source":
                    if (modules.Resolve<IWttSourceModule>(intent.Source.ChainType, intent.Source.Network) is { } source)
                        await source.SubmitWttSourceAsync(intent, WttLifecycleService.Read<WttStageQuote>(stage.QuoteJson), prepared, ct);
                    break;
                case "Target":
                    if (modules.Resolve<IWttTargetModule>(intent.Target.ChainType, intent.Target.Network) is { } target && operation.ProofsJson is not null)
                        await target.SubmitWttTargetAsync(intent, WttLifecycleService.Read<WttTargetProofs>(operation.ProofsJson),
                            WttLifecycleService.Read<WttStageQuote>(stage.QuoteJson), prepared, ct);
                    break;
                case "NativeOutput":
                    if (modules.Resolve<IWttNativeOutputModule>(intent.Target.ChainType, intent.Target.Network) is { } native && stage.ConsentJson is not null)
                        await native.SubmitWttNativeOutputAsync(intent, WttLifecycleService.Read<WttNativeOutputQuote>(stage.QuoteJson),
                            WttLifecycleService.Read<WttNativeOutputConsent>(stage.ConsentJson), prepared, ct);
                    break;
                case "Cleanup":
                    var network = await CleanupNetworkAsync(intent, stage, ct);
                    if (network is not null && modules.Resolve<IWttCleanupModule>(network.ChainType, network.Network) is { } cleanup
                        && stage.ConsentJson is not null)
                        await cleanup.SubmitWttCleanupAsync(intent, WttLifecycleService.Read<WttCleanupQuote>(stage.QuoteJson),
                            WttLifecycleService.Read<WttCleanupConsent>(stage.ConsentJson), prepared, ct);
                    break;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException)
        {
            // An ambiguous send keeps the exact authorized candidate; the next observation decides, never a new signature.
            logger.LogWarning(exception, "WTT submission of {Stage} for {OperationId} is ambiguous; retaining the saved candidate.",
                stage.Stage, stage.OperationId);
        }
    }

    private async Task<WttNetworkDescriptor?> CleanupNetworkAsync(WttOperationIntent intent, WttOperationStage stage, CancellationToken ct)
    {
        var original = stage.OriginalStageId is { } id ? await store.GetStageAsync(id, ct) : null;
        return original?.Phase switch { "Source" => intent.Source, "Target" => intent.Target, _ => null };
    }

    private (WttStageObservation, string)? Unobserved(WttOperationStage stage, string message)
    {
        logger.LogInformation("WTT observation of {Stage} for {OperationId} is unavailable: {Reason}", stage.Stage, stage.OperationId, message);
        return null;
    }

    private static string Canonical(object value) => CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(value));
}
