using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Wormhole;

public sealed partial class WttLifecycleService
{
    private sealed record NextStage(string Phase, string Stage, int Generation, WttStageQuote Quote, string? ParticipantAddress,
        long OperationVersion);

    /// <summary>Claims and prepares the next stage under current custody; optionally authorizes its first send.</summary>
    public Task<AZOAResult<WttOperationView>> ContinueAsync(string operationId, WttContinueCommand command,
        SigningCredentialWitness credential, CancellationToken ct = default)
        => ContinueCoreAsync(new(credential, null), operationId, command, ct);

    /// <summary>Workflow continuation of the node's own operation under its frozen witness.</summary>
    internal Task<AZOAResult<WttOperationView>> ContinueForWorkflowAsync(WorkflowSigningWitness witness, string operationId,
        WttContinueCommand command, CancellationToken ct = default)
        => ContinueCoreAsync(new(witness.Credential, witness), operationId, command, ct);

    private async Task<AZOAResult<WttOperationView>> ContinueCoreAsync(WttPrincipal principal, string operationId, WttContinueCommand command,
        CancellationToken ct)
    {
        if (command is null || command.ExpectedVersion < 0) return Changed<WttOperationView>();
        var operation = await ReadOwnedAsync(operationId, principal.Credential, false, ct);
        if (operation is null || operation.Version != command.ExpectedVersion) return Changed<WttOperationView>();
        if (AuthorityMismatch(operation, principal) is { } mismatch) return Forbidden<WttOperationView>(mismatch);
        var intent = Intent(operation);
        if (operation.State is not ("Admitted" or "Ready" or "Active"))
            return Conflict<WttOperationView>("No participant step is available while the operation is " + operation.State + "; refresh its state.");
        if (operation.State is "Admitted" or "Ready")
        {
            await recovery.ReconcileAsync(operationId, ct);
            operation = (await store.GetAsync(operationId, ct))!;
            if (operation.State is not ("Admitted" or "Ready")) return AZOAResult<WttOperationView>.Success(await ProjectAsync(operation, ct));
            await SealUnsentAsync(operation, await store.ReadStagesAsync(operationId, ct), ct);
            var next = await NextStageAsync(operationId, operation, intent, ct);
            if (next.IsError || next.Result is null) return AZOAResult<WttOperationView>.FailureWithCode(next.Message, next.Code ?? AzoaErrorCodes.Conflict);
            var claimed = await ClaimAsync(operationId, operation, intent, next.Result, principal, ct);
            if (claimed.IsError) return AZOAResult<WttOperationView>.FailureWithCode(claimed.Message, claimed.Code ?? AzoaErrorCodes.Conflict);
            operation = (await store.GetAsync(operationId, ct))!;
        }
        if (operation.State != "Active" || operation.CurrentStageId is not { } stageId)
            return AZOAResult<WttOperationView>.Success(await ProjectAsync(operation, ct));
        var current = await store.GetStageAsync(stageId, ct);
        if (current is null) return Changed<WttOperationView>();
        if (current.State is "Planned" or "Preparing" && current.ParticipantAddress is null && current.Phase is "Source" or "Target")
        {
            var prepared = await PrepareCustodialAsync(operationId, operation, intent, current, ct);
            if (prepared.IsError || prepared.Result is null)
                return AZOAResult<WttOperationView>.FailureWithCode(prepared.Message, prepared.Code ?? AzoaErrorCodes.Conflict);
            current = prepared.Result;
            operation = (await store.GetAsync(operationId, ct))!;
        }
        if (command.AuthorizeSubmission && current.State == "Prepared")
        {
            var authorized = await AuthorizeCurrentAsync(operationId, operation, intent, current, principal, ct);
            if (authorized.IsError) return AZOAResult<WttOperationView>.FailureWithCode(authorized.Message, authorized.Code ?? AzoaErrorCodes.Conflict);
        }
        return AZOAResult<WttOperationView>.Success(await ProjectAsync((await store.GetAsync(operationId, ct))!, ct));
    }

    /// <summary>Authorizes first send of the current saved candidate under the stage's live authority.</summary>
    public async Task<AZOAResult<WttOperationView>> SubmitAsync(string operationId, WttStageCommand command,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        var selected = await CommandAsync(operationId, command, credential, ct);
        if (selected is null) return Changed<WttOperationView>();
        var (operation, stage) = selected.Value;
        if (stage.PreparedJson is null || stage.State is not ("Prepared" or "Exposed")) return Changed<WttOperationView>();
        var authorized = await AuthorizeCurrentAsync(operationId, operation, Intent(operation), stage, new(credential, null), ct);
        return authorized.IsError ? AZOAResult<WttOperationView>.FailureWithCode(authorized.Message, authorized.Code ?? AzoaErrorCodes.Conflict)
            : AZOAResult<WttOperationView>.Success(await ProjectAsync((await store.GetAsync(operationId, ct))!, ct));
    }

    /// <summary>Records exposure, then discloses the exact unsigned participant message; replays the same message after response loss.</summary>
    public async Task<AZOAResult<WttParticipantSigningView>> ExposeParticipantAsync(string operationId, WttStageCommand command,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        var selected = await CommandAsync(operationId, command, credential, ct, allowAnyVersion: true);
        if (selected is null) return Changed<WttParticipantSigningView>();
        var (operation, stage) = selected.Value;
        if (stage.ParticipantAddress is null) return Invalid<WttParticipantSigningView>("This stage does not require a participant signature.");
        var intent = Intent(operation);
        if (stage.ExposedAt is null)
        {
            if (operation.Version != command.ExpectedVersion || stage.Version != command.ExpectedStageVersion || stage.State != "Planned")
                return Changed<WttParticipantSigningView>();
            AZOAResult<WttParticipantPreparation> participant;
            if (stage.Phase == "NativeOutput")
            {
                var module = modules.Resolve<IWttNativeOutputModule>(intent.Target.ChainType, intent.Target.Network);
                if (module is null) return Unavailable<WttParticipantSigningView>("The target native-output module is unavailable.");
                participant = await module.PrepareWttNativeOutputAsync(intent, Read<WttNativeOutputQuote>(stage.QuoteJson),
                    Read<WttNativeOutputConsent>(stage.ConsentJson!), stage.Generation, ct);
            }
            else
            {
                var module = modules.Resolve<IWttParticipantModule>(intent.Target.ChainType, intent.Target.Network);
                if (module is null) return Unavailable<WttParticipantSigningView>("The target participant module is unavailable.");
                participant = await module.PrepareWttParticipantAsync(intent, Read<WttStageQuote>(stage.QuoteJson), stage.Stage, stage.Generation, ct);
            }
            if (participant.IsError || participant.Result is not { } message) return Invalid<WttParticipantSigningView>(participant.Message);
            if (message.OperationId != intent.OperationId || message.Stage != stage.Stage || message.Generation != stage.Generation
                || message.ParticipantAddress != stage.ParticipantAddress || string.IsNullOrEmpty(message.UnsignedTransactionBase64))
                return Invalid<WttParticipantSigningView>("The provider participant message differs from the claimed stage.");
            var consent = await authorities.BindConsentAsync(credential, [AzoaScopes.WttParticipantSign], ct);
            if (consent.IsError || consent.Result is null) return Forbidden<WttParticipantSigningView>(consent.Message);
            var exposed = await store.ExposeAsync(operationId, operation.Version, command.StageId, stage.Version,
                Canonical(message), new(AzoaScopes.WttParticipantSign, null, consent.Result), ct);
            if (exposed is null) return Changed<WttParticipantSigningView>();
            stage = exposed;
            operation = (await store.GetAsync(operationId, ct))!;
        }
        var saved = Read<WttParticipantPreparation>(stage.ParticipantJson!);
        return AZOAResult<WttParticipantSigningView>.Success(new(operationId, command.StageId, operation.Version, stage.Version,
            saved.ParticipantAddress, saved.ProtocolIdentity, saved.UnsignedTransactionBase64, saved.Reservation));
    }

    /// <summary>Accepts only the participant's exact signature over the disclosed message, then optionally authorizes its first send.</summary>
    public async Task<AZOAResult<WttOperationView>> AcceptParticipantAsync(string operationId, WttParticipantSignatureCommand command,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (command?.SignedTransactionBase64 is not { Length: > 0 and <= 1048576 }) return Invalid<WttOperationView>("A signed participant transaction is required.");
        var selected = await CommandAsync(operationId, new(command.ExpectedVersion, command.StageId, command.ExpectedStageVersion), credential, ct);
        if (selected is null) return Changed<WttOperationView>();
        var (operation, stage) = selected.Value;
        if (stage.ExposedAt is null || stage.State != "Exposed" || stage.ParticipantJson is null) return Changed<WttOperationView>();
        var intent = Intent(operation);
        var participant = Read<WttParticipantPreparation>(stage.ParticipantJson);
        AZOAResult<WttStagePreparation> accepted;
        if (stage.Phase == "NativeOutput")
        {
            var module = modules.Resolve<IWttNativeOutputModule>(intent.Target.ChainType, intent.Target.Network);
            if (module is null) return Unavailable<WttOperationView>("The target native-output module is unavailable.");
            accepted = await module.AcceptWttNativeOutputSignatureAsync(intent, Read<WttNativeOutputQuote>(stage.QuoteJson),
                Read<WttNativeOutputConsent>(stage.ConsentJson!), participant, command.SignedTransactionBase64, ct);
        }
        else
        {
            var module = modules.Resolve<IWttParticipantModule>(intent.Target.ChainType, intent.Target.Network);
            if (module is null) return Unavailable<WttOperationView>("The target participant module is unavailable.");
            accepted = await module.AcceptWttParticipantSignatureAsync(intent, Read<WttStageQuote>(stage.QuoteJson), participant,
                command.SignedTransactionBase64, ct);
        }
        if (accepted.IsError || accepted.Result is not { } prepared || !ValidPrepared(prepared, stage))
            return Invalid<WttOperationView>(accepted.Message);
        var saved = await store.SaveParticipantSignatureAsync(operationId, command.ExpectedVersion, command.StageId,
            command.ExpectedStageVersion, Canonical(prepared), ct);
        if (saved is null) return Changed<WttOperationView>();
        if (command.AuthorizeSubmission)
        {
            var authorized = await AuthorizeCurrentAsync(operationId, (await store.GetAsync(operationId, ct))!, intent, saved,
                new(credential, null), ct);
            if (authorized.IsError) return AZOAResult<WttOperationView>.FailureWithCode(authorized.Message, authorized.Code ?? AzoaErrorCodes.Conflict);
        }
        return AZOAResult<WttOperationView>.Success(await ProjectAsync((await store.GetAsync(operationId, ct))!, ct));
    }

    /// <summary>Observes and replays already-authorized wire; never signs or claims a new stage.</summary>
    public async Task<AZOAResult<WttOperationView>> RefreshAsync(string operationId, SigningCredentialWitness credential,
        CancellationToken ct = default)
    {
        if (!CanReadWtt(credential)) return Forbidden<WttOperationView>(MissingReadScope);
        var operation = await ReadOwnedAsync(operationId, credential, true, ct);
        if (operation is null) return NotFound<WttOperationView>();
        await recovery.ReconcileAsync(operationId, ct);
        return AZOAResult<WttOperationView>.Success(await ProjectAsync((await store.GetAsync(operationId, ct))!, ct));
    }

    private async Task<AZOAResult<NextStage>> NextStageAsync(string operationId, WttOperation operation, WttOperationIntent intent,
        CancellationToken ct)
    {
        if (operation.Phase is not ("Source" or "Target"))
            return Invalid<NextStage>("No custodial stage remains; continue through native output or settlement.");
        var history = await store.ReadStagesAsync(operationId, ct);
        var phase = operation.Phase;
        var network = phase == "Source" ? intent.Source : intent.Target;
        var attempts = history.Where(stage => stage.Phase == phase).ToArray();
        var confirmed = attempts.Where(stage => stage.State == "Confirmed").Select(stage => stage.Stage).ToHashSet(StringComparer.Ordinal);
        WttTargetProofs? proofs = null;
        if (phase == "Target")
        {
            if (operation.ProofsJson is null) return Conflict<NextStage>("The source publication is still awaiting verified guardian proof.");
            proofs = Read<WttTargetProofs>(operation.ProofsJson);
        }
        var intentHash = IntentHashOf(operation);
        var admitted = phase == "Source" ? Read<WttStageQuote>(operation.SourceQuoteJson)
            : operation.TargetQuoteJson is { } targetQuote ? Read<WttStageQuote>(targetQuote) : null;
        var version = operation.Version;
        var now = clock.GetUtcNow();
        WttStageQuote quote;
        if (admitted is not null && admitted.ExpiresAt > now.AddSeconds(5) && NextName(admitted, confirmed) is { } admittedNext
            && attempts.All(stage => stage.Stage != admittedNext))
            quote = admitted;
        else
        {
            AZOAResult<WttStageQuote> fresh;
            if (phase == "Source")
            {
                var module = modules.Resolve<IWttSourceModule>(network.ChainType, network.Network);
                if (module is null) return Unavailable<NextStage>("The source WTT module is unavailable.");
                fresh = await module.QuoteWttSourceAsync(intent, ct);
            }
            else
            {
                var module = modules.Resolve<IWttTargetModule>(network.ChainType, network.Network);
                if (module is null) return Unavailable<NextStage>("The target WTT module is unavailable.");
                fresh = await module.QuoteWttTargetAsync(intent, proofs!, ct);
            }
            if (fresh.IsError || fresh.Result is null) return Conflict<NextStage>(fresh.Message);
            quote = fresh.Result;
            if (quote.IntentHash != intentHash || quote.Phase != phase || quote.ProtocolIdentity != network.ProtocolIdentity
                || quote.ExpiresAt <= now || quote.ExpiresAt > now.Add(MaximumOfferLifetime))
                return Conflict<NextStage>("The continuation quote changed the frozen intent or its validity window.");
            if (!WithinBudget(phase == "Source" ? intent.SourceBudget : intent.TargetBudget, attempts, quote, confirmed))
                return Conflict<NextStage>("Cumulative stage costs would exceed the explicitly consented budget for this chain.");
            if (phase == "Target" && admitted is null)
            {
                var recorded = await store.UpdateAsync(operationId, version, new(TargetQuoteJson: Canonical(quote)), ct);
                if (recorded is null) return Changed<NextStage>();
                version = recorded.Version;
            }
        }
        if (NextName(quote, confirmed) is not { } name)
            return Conflict<NextStage>("Every planned stage is confirmed; settlement follows authoritative observation.");
        var reservation = quote.Stages.Single(value => value.Stage == name);
        var owner = phase == "Source" ? intent.SourceAuthority.Address : intent.TargetAuthority.Address;
        string? participant = null;
        if (reservation.ParticipantAddress is { } address && address != owner)
        {
            if (phase != "Target" || address != intent.RecipientAddress)
                return Invalid<NextStage>("The planned stage requires an unsupported external signer.");
            participant = address;
        }
        return AZOAResult<NextStage>.Success(new(phase, name, attempts.Count(stage => stage.Stage == name), quote, participant, version));
    }

    private async Task<AZOAResult<WttOperationStage>> ClaimAsync(string operationId, WttOperation operation, WttOperationIntent intent,
        NextStage next, WttPrincipal principal, CancellationToken ct)
    {
        var authority = await StageAuthorityAsync(intent, next.Phase, next.ParticipantAddress is not null, null, principal, ct);
        if (authority.IsError || authority.Result is null) return Forbidden<WttOperationStage>(authority.Message);
        var quoteJson = Canonical(next.Quote);
        var stage = new WttOperationStage
        {
            Id = WttOperationIdentity.Stage(operationId, next.Phase, next.Stage, next.Generation), OperationId = operationId,
            IntentHash = operation.IntentHash, Phase = next.Phase, Stage = next.Stage, Generation = next.Generation,
            PreviousStageId = operation.CurrentStageId, QuoteJson = quoteJson, QuoteHash = CanonicalJson.HashCanonical(quoteJson),
            ParticipantAddress = next.ParticipantAddress,
        };
        var claimed = await store.ClaimStageAsync(operationId, next.OperationVersion, stage, authority.Result, ct);
        return claimed is null ? Changed<WttOperationStage>() : AZOAResult<WttOperationStage>.Success(claimed);
    }

    private async Task<AZOAResult<WttOperationStage>> PrepareCustodialAsync(string operationId, WttOperation operation,
        WttOperationIntent intent, WttOperationStage stage, CancellationToken ct)
    {
        var quote = Read<WttStageQuote>(stage.QuoteJson);
        AZOAResult<WttStagePreparation> prepared;
        var network = stage.Phase == "Source" ? intent.Source : intent.Target;
        var nonces = modules.Resolve<IWttNonceReservationModule>(network.ChainType, network.Network);
        if (nonces is not null)
        {
            // The stage epoch moves before any nonce is reserved, so a concurrent cancel or re-entry loses its version.
            var begun = await store.BeginPreparationAsync(operationId, operation.Version, BareId(stage.Id), stage.Version, ct);
            if (begun is null) return Changed<WttOperationStage>();
            stage = begun;
            operation = (await store.GetAsync(operationId, ct))!;
        }
        if (stage.Phase == "Source")
        {
            var module = modules.Resolve<IWttSourceModule>(network.ChainType, network.Network);
            if (module is null) return Unavailable<WttOperationStage>("The source WTT module is unavailable.");
            prepared = await module.PrepareWttSourceAsync(intent, quote, stage.Stage, stage.Generation, ct);
        }
        else
        {
            var module = modules.Resolve<IWttTargetModule>(network.ChainType, network.Network);
            if (module is null || operation.ProofsJson is null) return Unavailable<WttOperationStage>("The target WTT module or verified proof is unavailable.");
            prepared = await module.PrepareWttTargetAsync(intent, Read<WttTargetProofs>(operation.ProofsJson), quote, stage.Stage, stage.Generation, ct);
        }
        if (prepared.IsError || prepared.Result is null || !ValidPrepared(prepared.Result, stage))
        {
            if (nonces is null)
            {
                await store.RetireStageAsync(operationId, operation.Version, BareId(stage.Id), stage.Version, prepared.Message, ct: ct);
                await SealUnsentAsync((await store.GetAsync(operationId, ct))!, await store.ReadStagesAsync(operationId, ct), ct);
            }
            else await ResolveFailedReservationAsync(operationId, intent, stage, nonces, prepared.Message, ct);
            return Conflict<WttOperationStage>(string.IsNullOrWhiteSpace(prepared.Message)
                ? "The provider returned a candidate for another stage." : prepared.Message);
        }
        var saved = await store.SavePreparedAsync(operationId, operation.Version, BareId(stage.Id), stage.Version,
            Canonical(prepared.Result), ct);
        return saved is null ? Changed<WttOperationStage>() : AZOAResult<WttOperationStage>.Success(saved);
    }

    private async Task<AZOAResult<WttOperationStage>> AuthorizeCurrentAsync(string operationId, WttOperation operation,
        WttOperationIntent intent, WttOperationStage stage, WttPrincipal principal, CancellationToken ct)
    {
        string? originalPhase = null;
        if (stage.Phase == "Cleanup")
            originalPhase = stage.OriginalStageId is { } originalId ? (await store.GetStageAsync(originalId, ct))?.Phase : null;
        var authority = await StageAuthorityAsync(intent, stage.Phase, stage.ParticipantAddress is not null, originalPhase, principal, ct);
        if (authority.IsError || authority.Result is null) return Forbidden<WttOperationStage>(authority.Message);
        if (await StageValueRefusalAsync(intent, stage.Phase, stage.Stage, principal, ct) is { } refused) return Forbidden<WttOperationStage>(refused);
        var authorized = await store.AuthorizeAsync(operationId, operation.Version, BareId(stage.Id), stage.Version, authority.Result, ct);
        if (authorized is null) return Changed<WttOperationStage>();
        await recovery.ReconcileAsync(operationId, ct);
        return AZOAResult<WttOperationStage>.Success(authorized);
    }

    private async Task<AZOAResult<WttStageAuthority>> StageAuthorityAsync(WttOperationIntent intent, string phase, bool participant,
        string? originalPhase, WttPrincipal principal, CancellationToken ct)
    {
        var credential = principal.Credential;
        if (principal.Workflow is not null && (participant || phase is "NativeOutput" or "Cleanup"))
            return Forbidden<WttStageAuthority>("A workflow node signs only its source and target payer stages.");
        if (participant || phase == "NativeOutput")
        {
            var consent = await authorities.BindConsentAsync(credential, [AzoaScopes.WttParticipantSign], ct);
            return consent.IsError || consent.Result is null ? Forbidden<WttStageAuthority>(consent.Message)
                : AZOAResult<WttStageAuthority>.Success(new(AzoaScopes.WttParticipantSign, null, consent.Result));
        }
        var (owner, network, scope) = phase switch
        {
            "Source" => (intent.SourceAuthority, intent.Source, AzoaScopes.WttSourceSign),
            "Target" => (intent.TargetAuthority, intent.Target, AzoaScopes.WttTargetSign),
            "Cleanup" when originalPhase == "Source" => (intent.SourceAuthority, intent.Source, AzoaScopes.WttCleanupSign),
            "Cleanup" when originalPhase == "Target" => (intent.TargetAuthority, intent.Target, AzoaScopes.WttCleanupSign),
            _ => (null!, null!, ""),
        };
        if (owner is null) return Forbidden<WttStageAuthority>("No signing authority applies to this stage.");
        if (phase == "Cleanup" && credential.TenantId is not null)
            return Forbidden<WttStageAuthority>("Residual return requires the original owner's direct credential.");
        var signing = await BindSigningAsync(principal, owner.Context.WalletId, network.ChainType, owner.Address, scope, ct);
        return signing.IsError || signing.Result is null ? Forbidden<WttStageAuthority>(signing.Message)
            : AZOAResult<WttStageAuthority>.Success(new(scope, signing.Result, null));
    }

    private async Task<(WttOperation Operation, WttOperationStage Stage)?> CommandAsync(string operationId, WttStageCommand command,
        SigningCredentialWitness credential, CancellationToken ct, bool allowAnyVersion = false)
    {
        if (command is null || command.ExpectedVersion < 0 || command.ExpectedStageVersion < 0
            || !WttOperationIdentity.IsCanonicalId(command.StageId)) return null;
        var operation = await ReadOwnedAsync(operationId, credential, false, ct);
        if (operation is null || operation.CurrentStageId != command.StageId || !allowAnyVersion && operation.Version != command.ExpectedVersion
            || SigningActionOf(operation) is not null)
            return null;
        var stage = await store.GetStageAsync(command.StageId, ct);
        return stage is null || stage.OperationId != operationId || stage.IntentHash != operation.IntentHash
            || !allowAnyVersion && stage.Version != command.ExpectedStageVersion ? null : (operation, stage);
    }

    /// <summary>After a failed durable-nonce preparation, frees a provably unsigned reservation and retires the stage at its own
    /// epoch; a signed or unknown reservation stays Preparing for idempotent re-entry.</summary>
    private async Task ResolveFailedReservationAsync(string operationId, WttOperationIntent intent, WttOperationStage stage,
        IWttNonceReservationModule nonces, string detail, CancellationToken ct)
    {
        var hold = await nonces.ReadWttNonceHoldAsync(intent, stage.Phase, stage.Stage, stage.Generation, ct);
        if (hold == WttNonceHold.Unsigned && await nonces.ReleaseUnsignedWttReservationAsync(intent, stage.Phase, stage.Stage, stage.Generation, ct))
            hold = WttNonceHold.None;
        if (hold != WttNonceHold.None || await store.GetAsync(operationId, ct) is not { } operation) return;
        await store.RetireStageAsync(operationId, operation.Version, BareId(stage.Id), stage.Version, detail, reservationProbedFree: true, ct);
    }

    /// <summary>Idempotently seals every never-sent retired or cancelled custodial generation.</summary>
    private async Task SealUnsentAsync(WttOperation operation, IReadOnlyList<WttOperationStage> stages, CancellationToken ct)
    {
        foreach (var stage in stages.Where(candidate => candidate.State is "Retired" or "Cancelled" && candidate.AuthorizedAt is null
            && candidate.ExposedAt is null))
            await SealAsync(operation, stage, ct);
    }

    private async Task SealAsync(WttOperation operation, WttOperationStage stage, CancellationToken ct)
    {
        if (stage.Phase is not ("Source" or "Target")) return;
        var quote = Read<WttStageQuote>(stage.QuoteJson);
        var intent = IntentFor(operation, quote.IntentHash);
        var network = stage.Phase == "Source" ? intent.Source : intent.Target;
        // A durable-nonce chain's signed candidate owns its nonce; its recovery layer resolves it, never a seal.
        if (modules.Resolve<IWttNonceReservationModule>(network.ChainType, network.Network) is not null) return;
        var binding = new WttPreparationBinding(quote.ProtocolIdentity, intent.ActorUserId, intent.TenantId, intent.OperationId,
            stage.Phase, stage.Stage, stage.Generation, quote.IntentHash, quote.QuoteId);
        await preparations.SealAsync(WttPreparationIdentity.Id(binding), Canonical(binding), ct);
    }

    private static string? NextName(WttStageQuote quote, IReadOnlySet<string> confirmed)
        => quote.Stages.Select(stage => stage.Stage).FirstOrDefault(stage => !stage.StartsWith("Cleanup:", StringComparison.Ordinal)
            && !confirmed.Contains(stage));

    private static bool ValidPrepared(WttStagePreparation prepared, WttOperationStage stage)
        => prepared.Stage == stage.Stage && prepared.Generation == stage.Generation && prepared.Reservation?.Stage == stage.Stage
            && !string.IsNullOrWhiteSpace(prepared.TransactionId) && !string.IsNullOrWhiteSpace(prepared.SignedTransactionBase64);

    /// <summary>Potentially spent attempts plus every unconfirmed continuation reservation must fit each independent cap.</summary>
    private static bool WithinBudget(WttChainBudget budget, IReadOnlyList<WttOperationStage> attempts, WttStageQuote quote,
        IReadOnlySet<string> confirmed)
    {
        BigInteger fee = 0, deposit = 0, value = 0;
        foreach (var attempt in attempts.Where(stage => stage.State is not ("Retired" or "Cancelled" or "ExpiredNoEffect")))
        {
            var reservation = attempt.PreparedJson is not null ? Read<WttStagePreparation>(attempt.PreparedJson).Reservation
                : Read<WttStageQuote>(attempt.QuoteJson).Stages.FirstOrDefault(stage => stage.Stage == attempt.Stage);
            if (reservation is null) return false;
            fee += WttPayloadCodec.ParseAmount(reservation.NetworkFee);
            deposit += WttPayloadCodec.ParseAmount(reservation.LockedDeposit);
            value += WttPayloadCodec.ParseAmount(reservation.Value);
        }
        foreach (var reservation in quote.Stages.Where(stage => !confirmed.Contains(stage.Stage)))
        {
            fee += WttPayloadCodec.ParseAmount(reservation.NetworkFee);
            deposit += WttPayloadCodec.ParseAmount(reservation.LockedDeposit);
            value += WttPayloadCodec.ParseAmount(reservation.Value);
        }
        return fee <= WttPayloadCodec.ParseAmount(budget.MaximumNetworkFee) && deposit <= WttPayloadCodec.ParseAmount(budget.MaximumLockedDeposit)
            && value <= WttPayloadCodec.ParseAmount(budget.MaximumValue);
    }

    /// <summary>Refusal when a direct credential reaches a workflow-owned operation, or a workflow reaches another operation.</summary>
    private static string? AuthorityMismatch(WttOperation operation, WttPrincipal principal)
        => operation.OwnerIntentJson is not null && (principal.Credential.TenantId is not null || principal.Workflow is not null)
            ? "The owner took over this transfer; only the owner's direct credential can continue it."
            : SigningActionOf(operation) != principal.Action
                ? operation.ActionRunId is null ? "A workflow node can only continue the operation it admitted."
                    : "This operation's signing belongs to the workflow node that admitted it."
                : null;

    private static string BareId(string id) => SurrealRecordGuid.BareId(id);
}
