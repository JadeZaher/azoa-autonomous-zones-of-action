using System.Globalization;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Wormhole;

public sealed partial class WttLifecycleService
{
    private sealed record CleanupContext(WttCleanupQuote Quote, WttOperationStage Original, IWttCleanupModule Module,
        WttTargetProofs? Proofs, WttNetworkDescriptor Network);

    /// <summary>Discloses the exact native-output economics after finalized redemption; grants no signing authority.</summary>
    public async Task<AZOAResult<WttNativeOutputOffer>> QuoteNativeOutputAsync(string operationId, SigningCredentialWitness credential,
        CancellationToken ct = default)
    {
        if (!CanReadWtt(credential)) return Forbidden<WttNativeOutputOffer>(MissingReadScope);
        var operation = await ReadOwnedAsync(operationId, credential, false, ct);
        if (operation is null) return NotFound<WttNativeOutputOffer>();
        var quoted = await NativeQuoteAsync(operationId, operation, ct);
        if (quoted.IsError || quoted.Result is not { } quote)
            return AZOAResult<WttNativeOutputOffer>.FailureWithCode(quoted.Message, quoted.Code ?? AzoaErrorCodes.Conflict);
        return AZOAResult<WttNativeOutputOffer>.Success(new(quote.NativeAmount, quote.NetworkFee, quote.TemporaryRent,
            quote.RecipientAddress, quote.ExpiresAt));
    }

    /// <summary>Claims a recipient native-output stage under fresh explicit cost ceilings; the recipient still signs its exact message.</summary>
    public async Task<AZOAResult<WttOperationView>> StartNativeOutputAsync(string operationId, WttNativeOutputRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (request is null || !request.AcceptNativeOutput || !WttOperationIdentity.ValidRequestKey(request.RequestKey)
            || !UInt64Text.TryParse(request.MaximumNetworkFee, out var maximumFee) || !UInt64Text.TryParse(request.MaximumTemporaryRent, out var maximumRent))
            return Invalid<WttOperationView>("Explicit native-output acceptance, a request key and both cost ceilings are required.");
        var operation = await ReadOwnedAsync(operationId, credential, false, ct);
        if (operation is null || operation.Version != request.ExpectedVersion || SigningActionOf(operation) is not null) return Changed<WttOperationView>();
        if (await StageValueRefusalAsync(Intent(operation), "NativeOutput", "Unwrap", new(credential, null), ct) is { } refused)
            return Forbidden<WttOperationView>(refused);
        var quoted = await NativeQuoteAsync(operationId, operation, ct);
        if (quoted.IsError || quoted.Result is not { } quote)
            return AZOAResult<WttOperationView>.FailureWithCode(quoted.Message, quoted.Code ?? AzoaErrorCodes.Conflict);
        if (UInt64Text.Parse(quote.NetworkFee) > maximumFee || UInt64Text.Parse(quote.TemporaryRent) > maximumRent)
            return Conflict<WttOperationView>("Current native-output costs exceed the accepted ceilings.");
        var intent = Intent(operation);
        var consent = new WttNativeOutputConsent(1, request.RequestKey, quote.QuoteId, request.MaximumNetworkFee, request.MaximumTemporaryRent);
        var stageName = "Unwrap:" + CanonicalJson.Hash(new { consent.RequestKey });
        var history = await store.ReadStagesAsync(operationId, ct);
        var generation = history.Count(stage => stage.Phase == "NativeOutput" && stage.Stage == stageName);
        var authority = await StageAuthorityAsync(intent, "NativeOutput", true, null, new(credential, null), ct);
        if (authority.IsError || authority.Result is null) return Forbidden<WttOperationView>(authority.Message);
        var quoteJson = Canonical(quote);
        var claimed = await store.ClaimStageAsync(operationId, operation.Version, new WttOperationStage
        {
            Id = WttOperationIdentity.Stage(operationId, "NativeOutput", stageName, generation), OperationId = operationId,
            IntentHash = operation.IntentHash, Phase = "NativeOutput", Stage = stageName, Generation = generation,
            PreviousStageId = operation.CurrentStageId, QuoteJson = quoteJson, QuoteHash = CanonicalJson.HashCanonical(quoteJson),
            ConsentJson = Canonical(consent), ParticipantAddress = quote.RecipientAddress,
        }, authority.Result, ct);
        return claimed is null ? Changed<WttOperationView>()
            : AZOAResult<WttOperationView>.Success(await ProjectAsync((await store.GetAsync(operationId, ct))!, ct));
    }

    /// <summary>Discloses a residual return for one resolved original stage; read-only and separate from the original workflow.</summary>
    public async Task<AZOAResult<WttCleanupOffer>> QuoteCleanupAsync(string operationId, string stageId, SigningCredentialWitness credential,
        CancellationToken ct = default)
    {
        if (!CanReadWtt(credential)) return Forbidden<WttCleanupOffer>(MissingReadScope);
        var operation = await ReadOwnedAsync(operationId, credential, true, ct);
        if (operation is null) return NotFound<WttCleanupOffer>();
        var context = await CleanupContextAsync(operationId, operation, stageId, ct);
        if (context.IsError || context.Result is not { } cleanup)
            return AZOAResult<WttCleanupOffer>.FailureWithCode(context.Message, context.Code ?? AzoaErrorCodes.Conflict);
        var quote = cleanup.Quote;
        return AZOAResult<WttCleanupOffer>.Success(new(stageId, quote.ReturnAmount, quote.OriginalAttributedDepositReturn,
            quote.NetworkFee, quote.DestinationAddress, quote.ExpiresAt));
    }

    /// <summary>Admits, prepares and authorizes a fresh direct-owner residual return after every original attempt is resolved.</summary>
    public async Task<AZOAResult<WttOperationView>> StartCleanupAsync(string operationId, WttCleanupRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (request is null || !request.AcceptReturn || !WttOperationIdentity.ValidRequestKey(request.RequestKey)
            || !UInt64Text.TryParse(request.MaximumNetworkFee, out var maximumFee))
            return Invalid<WttOperationView>("Explicit residual-return acceptance, a request key and a fee ceiling are required.");
        if (credential.TenantId is not null) return Forbidden<WttOperationView>("Residual return requires the original owner's direct credential.");
        var operation = await ReadOwnedAsync(operationId, credential, true, ct);
        if (operation is null || operation.Version != request.ExpectedVersion) return Changed<WttOperationView>();
        if (operation.State is not ("Ready" or "AwaitingProof" or "Settled"))
            return Conflict<WttOperationView>("Every original attempt must be resolved before a residual return.");
        var context = await CleanupContextAsync(operationId, operation, request.StageId, ct);
        if (context.IsError || context.Result is not { } cleanup)
            return AZOAResult<WttOperationView>.FailureWithCode(context.Message, context.Code ?? AzoaErrorCodes.Conflict);
        var quote = cleanup.Quote;
        if (UInt64Text.Parse(quote.ReturnAmount) == 0) return Conflict<WttOperationView>("No residual balance remains in this stage account.");
        if (UInt64Text.Parse(quote.NetworkFee) > maximumFee) return Conflict<WttOperationView>("The residual-return fee exceeds the accepted ceiling.");
        var intent = IntentFor(operation, Read<WttStageQuote>(cleanup.Original.QuoteJson).IntentHash);
        var consent = new WttCleanupConsent(1, request.RequestKey, quote.QuoteId, request.MaximumNetworkFee);
        var stageName = quote.Phase + ":" + quote.OriginalStage + ":" + quote.OriginalGeneration.ToString(CultureInfo.InvariantCulture)
            + ":" + CanonicalJson.Hash(new { consent.RequestKey });
        var history = await store.ReadStagesAsync(operationId, ct);
        var generation = history.Count(stage => stage.Phase == "Cleanup" && stage.Stage == stageName);
        var authority = await StageAuthorityAsync(Intent(operation), "Cleanup", false, cleanup.Original.Phase, new(credential, null), ct);
        if (authority.IsError || authority.Result is null) return Forbidden<WttOperationView>(authority.Message);
        var quoteJson = Canonical(quote);
        var claimed = await store.ClaimStageAsync(operationId, operation.Version, new WttOperationStage
        {
            Id = WttOperationIdentity.Stage(operationId, "Cleanup", stageName, generation), OperationId = operationId,
            IntentHash = operation.IntentHash, Phase = "Cleanup", Stage = stageName, Generation = generation,
            PreviousStageId = operation.CurrentStageId, OriginalStageId = BareId(cleanup.Original.Id), QuoteJson = quoteJson,
            QuoteHash = CanonicalJson.HashCanonical(quoteJson), ConsentJson = Canonical(consent),
        }, authority.Result, ct);
        if (claimed is null) return Changed<WttOperationView>();
        var owner = cleanup.Original.Phase == "Source" ? intent.SourceAuthority : intent.TargetAuthority;
        var signer = new WttSigningAuthority(owner.Address, new SigningContext(intent.ActorUserId, owner.Context.WalletId, false,
            Scope: AzoaScopes.WttCleanupSign, ChainType: cleanup.Network.ChainType));
        var prepared = await cleanup.Module.PrepareWttCleanupAsync(intent, Read<WttStageQuote>(cleanup.Original.QuoteJson), cleanup.Proofs,
            Read<WttStagePreparation>(cleanup.Original.PreparedJson!), quote, consent, signer, generation, ct);
        operation = (await store.GetAsync(operationId, ct))!;
        if (prepared.IsError || prepared.Result is null || !ValidPrepared(prepared.Result, claimed))
        {
            await store.RetireStageAsync(operationId, operation.Version, BareId(claimed.Id), claimed.Version, prepared.Message, ct: ct);
            return Conflict<WttOperationView>(prepared.Message);
        }
        var saved = await store.SavePreparedAsync(operationId, operation.Version, BareId(claimed.Id), claimed.Version,
            Canonical(prepared.Result), ct);
        if (saved is null) return Changed<WttOperationView>();
        var authorized = await AuthorizeCurrentAsync(operationId, (await store.GetAsync(operationId, ct))!, intent, saved, new(credential, null), ct);
        if (authorized.IsError) return AZOAResult<WttOperationView>.FailureWithCode(authorized.Message, authorized.Code ?? AzoaErrorCodes.Conflict);
        return AZOAResult<WttOperationView>.Success(await ProjectAsync((await store.GetAsync(operationId, ct))!, ct));
    }

    private async Task<AZOAResult<WttNativeOutputQuote>> NativeQuoteAsync(string operationId, WttOperation operation, CancellationToken ct)
    {
        if (operation.Phase != "NativeOutput" || operation.State != "Ready" || operation.ProofsJson is null)
            return Conflict<WttNativeOutputQuote>("Native output is available only after finalized redemption of a native-output transfer.");
        var history = await store.ReadStagesAsync(operationId, ct);
        var redeem = history.LastOrDefault(stage => stage.Phase == "Target" && stage.Stage == "Redeem" && stage.State == "Confirmed");
        if (redeem?.PreparedJson is null) return Conflict<WttNativeOutputQuote>("The finalized original redemption is unavailable.");
        var intent = IntentFor(operation, Read<WttStageQuote>(redeem.QuoteJson).IntentHash);
        var module = modules.Resolve<IWttNativeOutputModule>(intent.Target.ChainType, intent.Target.Network);
        if (module is null) return Unavailable<WttNativeOutputQuote>("The target native-output module is unavailable.");
        var quoted = await module.QuoteWttNativeOutputAsync(intent, Read<WttTargetProofs>(operation.ProofsJson),
            Read<WttStageQuote>(redeem.QuoteJson), Read<WttStagePreparation>(redeem.PreparedJson), ct);
        return quoted.IsError || quoted.Result is null ? Conflict<WttNativeOutputQuote>(quoted.Message) : quoted;
    }

    private async Task<AZOAResult<CleanupContext>> CleanupContextAsync(string operationId, WttOperation operation, string stageId,
        CancellationToken ct)
    {
        if (!WttOperationIdentity.IsCanonicalId(stageId)) return Invalid<CleanupContext>("A canonical original stage identity is required.");
        var original = await store.GetStageAsync(stageId, ct);
        if (original is null || original.OperationId != operationId || original.IntentHash != operation.IntentHash
            || original.Phase is not ("Source" or "Target") || original.State is not ("Confirmed" or "FailedOnChain") || original.PreparedJson is null)
            return Conflict<CleanupContext>("Only a resolved original stage with its saved signed candidate can return residual funds.");
        var intent = IntentFor(operation, Read<WttStageQuote>(original.QuoteJson).IntentHash);
        var network = original.Phase == "Source" ? intent.Source : intent.Target;
        var module = modules.Resolve<IWttCleanupModule>(network.ChainType, network.Network);
        if (module is null) return Unavailable<CleanupContext>("Residual return applies only to chains with protected stage payers.");
        var proofs = original.Phase == "Target" && operation.ProofsJson is { } stored ? Read<WttTargetProofs>(stored) : null;
        var quoted = await module.QuoteWttCleanupAsync(intent, Read<WttStageQuote>(original.QuoteJson), proofs,
            Read<WttStagePreparation>(original.PreparedJson), ct);
        if (quoted.IsError || quoted.Result is null) return Conflict<CleanupContext>(quoted.Message);
        return AZOAResult<CleanupContext>.Success(new(quoted.Result, original, module, proofs, network));
    }
}
