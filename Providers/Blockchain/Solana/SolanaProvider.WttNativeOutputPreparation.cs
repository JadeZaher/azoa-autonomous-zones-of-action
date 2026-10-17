using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private sealed record WttNativeOutputWirePlan(DurableSolanaPlan Recovery, string MessageBase64);

    /// <inheritdoc cref="IWttNativeOutputModule.PrepareWttNativeOutputAsync"/>
    public async Task<AZOAResult<WttParticipantPreparation>> PrepareWttNativeOutputAsync(WttOperationIntent intent,
        WttNativeOutputQuote quote, WttNativeOutputConsent consent, int generation, CancellationToken ct = default)
    {
        try
        {
            var plan = ReadWttNativeOutputQuote(intent, quote);
            var binding = WttNativeOutputBinding(intent, quote, consent, generation);
            var id = WttPreparationIdentity.Id(binding);
            using var scope = _custodyScopeFactory?.CreateScope();
            var store = _wttPreparationStore ?? scope?.ServiceProvider.GetRequiredService<IWttPreparationStore>()
                ?? throw new InvalidOperationException("Durable native-output preparation storage is required.");
            var saved = await store.GetAsync(id, ct);
            if (saved is null)
            {
                if (quote.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidDataException("Native-output offer expired before message admission.");
                var current = await QuoteWttNativeOutputAsync(intent, plan.Proofs, plan.TargetQuote, plan.Redemption, ct);
                if (current.IsError || current.Result is null || current.Result.RedemptionObservationHash != quote.RedemptionObservationHash
                    || current.Result.NativeAmount != quote.NativeAmount || current.Result.TemporaryRent != quote.TemporaryRent
                    || UInt64Text.Parse(current.Result.NetworkFee) > UInt64Text.Parse(quote.NetworkFee)
                    || ReadWttNativeOutputQuote(intent, current.Result).Guard != plan.Guard)
                    throw new InvalidDataException("Original redemption or native-output economics changed before message creation.");
                var recovery = await NewPlanAsync("", 0, 0, false, binding.IntentHash, ct);
                if ("solana:" + recovery.Genesis != quote.ProtocolIdentity)
                    throw new InvalidDataException("Native output belongs to another physical network.");
                var built = WttSolanaNativeOutputWire.Build(intent, quote, id, new(plan.Guard.Program),
                    Convert.FromBase64String(plan.Guard.RentBase64), plan.ComputeUnits, plan.ComputePrice);
                await RequireWttNativeOutputAccountsAsync(quote, built.Account.Address, plan.RedemptionSlot, ct);
                var message = CompileWttNativeOutput(intent, quote, plan, recovery, id);
                if (await MeasureSolanaComputeAsync(message, checked((long)plan.RedemptionSlot), ct) > plan.ComputeUnits
                    || await NativeMessageFeeAsync(message, ct) > UInt64Text.Parse(quote.NetworkFee) || quote.ExpiresAt <= DateTimeOffset.UtcNow)
                    throw new InvalidDataException("Exact recipient message exceeds its accepted validity or cost limits.");
                saved = await store.ClaimAsync(new WttPreparation { Id = id,
                    BindingJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(binding)),
                    PlanJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new WttNativeOutputWirePlan(recovery, Convert.ToBase64String(message)))),
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow }, ct);
            }
            var winner = ReadWttNativeOutputWire(intent, quote, plan, binding, saved);
            if (await VerifyGenesisAsync(ct) != winner.Recovery.Genesis) throw new InvalidDataException("Saved recipient message belongs to another genesis.");
            return Ok(new WttParticipantPreparation(intent.OperationId, binding.Stage, generation, quote.RecipientAddress,
                quote.ProtocolIdentity, winner.MessageBase64, saved.PlanJson, WttNativeOutputReservation(binding, quote)));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException
            or FormatException or KeyNotFoundException or InvalidOperationException or OverflowException)
        { return Error<WttParticipantPreparation>(exception.Message); }
    }

    /// <inheritdoc cref="IWttNativeOutputModule.AcceptWttNativeOutputSignatureAsync"/>
    public async Task<AZOAResult<WttStagePreparation>> AcceptWttNativeOutputSignatureAsync(WttOperationIntent intent,
        WttNativeOutputQuote quote, WttNativeOutputConsent consent, WttParticipantPreparation prepared,
        string signedTransactionBase64, CancellationToken ct = default)
    {
        try
        {
            var plan = ReadWttNativeOutputQuote(intent, quote);
            var binding = WttNativeOutputBinding(intent, quote, consent, prepared.Generation);
            using var scope = _custodyScopeFactory?.CreateScope();
            var store = _wttPreparationStore ?? scope?.ServiceProvider.GetRequiredService<IWttPreparationStore>()
                ?? throw new InvalidOperationException("Durable native-output preparation storage is required.");
            var saved = await store.GetAsync(WttPreparationIdentity.Id(binding), ct)
                ?? throw new InvalidDataException("Recipient message must be durably admitted before accepting its signature.");
            var wire = ReadWttNativeOutputWire(intent, quote, plan, binding, saved);
            var reservation = WttNativeOutputReservation(binding, quote);
            if (prepared != new WttParticipantPreparation(intent.OperationId, binding.Stage, binding.Generation, quote.RecipientAddress,
                    quote.ProtocolIdentity, wire.MessageBase64, saved.PlanJson, reservation))
                throw new InvalidDataException("Recipient signature request changed its exact disclosed intent.");
            var bytes = Convert.FromBase64String(signedTransactionBase64);
            var transactionId = SolanaMessageEnvelope.VerifyWire(bytes, Convert.FromBase64String(wire.MessageBase64));
            if (Convert.ToBase64String(bytes) != signedTransactionBase64)
                throw new InvalidDataException("Recipient signed envelope is not canonical base64.");
            var candidate = new WttStagePreparation(binding.Stage, binding.Generation, transactionId, signedTransactionBase64,
                binding.ProtocolIdentity, saved.PlanJson, reservation);
            if (saved.SignedPreparationJson is null)
                saved = await store.TrySaveSignedAsync(saved, CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(candidate)), ct)
                    ?? await store.GetAsync(saved.Id, ct) ?? throw new InvalidDataException("Recipient candidate admission disappeared.");
            var exact = ReadWttNativeOutputSigned(intent, quote, plan, binding, saved);
            if (exact != candidate) throw new InvalidDataException("This generation already has a different recipient signature.");
            return Ok(exact);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException
            or FormatException or KeyNotFoundException or InvalidOperationException or OverflowException)
        { return Error<WttStagePreparation>(exception.Message); }
    }

    private static WttSolanaNativeOutputPlan ReadWttNativeOutputQuote(WttOperationIntent intent, WttNativeOutputQuote quote)
    {
        if (quote.Version != 1 || quote.IntentHash != CanonicalJson.Hash(intent) || !intent.UnwrapNative
            || quote.QuoteId != CanonicalJson.Hash(quote with { QuoteId = "" }))
            throw new InvalidDataException("Native-output offer differs from the exact original intent.");
        var plan = JsonSerializer.Deserialize<WttSolanaNativeOutputPlan>(quote.PlanJson)
            ?? throw new InvalidDataException("Native-output proof is missing.");
        if (plan.Version != 1 || plan.Guard is null || plan.Proofs is null || plan.TargetQuote is null || plan.Redemption is null
            || CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan)) != quote.PlanJson
            || plan.Redemption.Stage != "Redeem" || CanonicalJson.Hash(plan.Redemption) != quote.RedemptionPreparationHash
            || plan.TargetQuote.Phase != "Target" || plan.TargetQuote.IntentHash != quote.IntentHash
            || plan.Redemption.ProtocolIdentity != quote.ProtocolIdentity || plan.TargetQuote.ProtocolIdentity != quote.ProtocolIdentity
            || quote.RedemptionObservationHash.Length != 64 || string.IsNullOrWhiteSpace(plan.RedemptionBlockIdentity))
            throw new InvalidDataException("Native-output offer lost its original redemption identity.");
        _ = UInt64Text.Parse(quote.NetworkFee);
        return plan;
    }

    private static WttPreparationBinding WttNativeOutputBinding(WttOperationIntent intent, WttNativeOutputQuote quote,
        WttNativeOutputConsent consent, int generation)
    {
        if (consent.Version != 1 || consent.QuoteId != quote.QuoteId || string.IsNullOrWhiteSpace(consent.RequestKey)
            || consent.RequestKey.Length > 200 || UInt64Text.Parse(consent.MaximumNetworkFee) < UInt64Text.Parse(quote.NetworkFee)
            || UInt64Text.Parse(consent.MaximumTemporaryRent) < UInt64Text.Parse(quote.TemporaryRent))
            throw new InvalidDataException("Native output requires exact recipient fee and temporary-rent consent.");
        return new(quote.ProtocolIdentity, intent.ActorUserId, intent.TenantId, intent.OperationId, "Participant",
            "Unwrap:" + CanonicalJson.Hash(new { consent.RequestKey }), generation, CanonicalJson.Hash(new { intent, quote, consent }), quote.QuoteId);
    }

    private static WttStageReservation WttNativeOutputReservation(WttPreparationBinding binding, WttNativeOutputQuote quote)
        => new(binding.Stage, quote.NetworkFee, quote.TemporaryRent, "0", quote.RecipientAddress);

    private static WttNativeOutputWirePlan ReadWttNativeOutputWire(WttOperationIntent intent, WttNativeOutputQuote quote,
        WttSolanaNativeOutputPlan plan, WttPreparationBinding binding, WttPreparation saved)
    {
        if (saved.Sealed || saved.BindingJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(binding)))
            throw new InvalidDataException("Native-output generation is sealed or belongs to another intent.");
        var wire = JsonSerializer.Deserialize<WttNativeOutputWirePlan>(saved.PlanJson)
            ?? throw new InvalidDataException("Native-output unsigned plan is missing.");
        if (wire.Recovery is null || wire.Recovery.IntentHash != binding.IntentHash || "solana:" + wire.Recovery.Genesis != binding.ProtocolIdentity
            || wire.Recovery.ContextSlot < 0 || wire.Recovery.LastValidBlockHeight <= 0
            || saved.PlanJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(wire))
            || wire.MessageBase64 != Convert.ToBase64String(CompileWttNativeOutput(intent, quote, plan, wire.Recovery, WttPreparationIdentity.Id(binding))))
            throw new InvalidDataException("Saved native output differs from its exact original recipient message.");
        var message = SolanaMessageEnvelope.Read(Convert.FromBase64String(wire.MessageBase64));
        if (message.RequiredSignatures != 1 || message.RequiredKeys.Single().Key != quote.RecipientAddress)
            throw new InvalidDataException("Native output requires only the actual recipient's signature.");
        return wire;
    }

    private static WttStagePreparation ReadWttNativeOutputSigned(WttOperationIntent intent, WttNativeOutputQuote quote,
        WttSolanaNativeOutputPlan plan, WttPreparationBinding binding, WttPreparation saved)
    {
        var wire = ReadWttNativeOutputWire(intent, quote, plan, binding, saved);
        var prepared = saved.SignedPreparationJson is null ? null : JsonSerializer.Deserialize<WttStagePreparation>(saved.SignedPreparationJson);
        if (prepared is null || prepared.Stage != binding.Stage || prepared.Generation != binding.Generation
            || prepared.ProtocolIdentity != binding.ProtocolIdentity || prepared.RecoveryMetadata != saved.PlanJson
            || prepared.Reservation != WttNativeOutputReservation(binding, quote) || prepared.AdditionalSignedTransactionsJson is not null
            || SolanaMessageEnvelope.VerifyWire(Convert.FromBase64String(prepared.SignedTransactionBase64), Convert.FromBase64String(wire.MessageBase64)) != prepared.TransactionId)
            throw new InvalidDataException("Native-output signed candidate differs from its durable winner.");
        return prepared;
    }
}
