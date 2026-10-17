using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using Microsoft.Extensions.DependencyInjection;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider : IWttParticipantModule
{
    /// <inheritdoc cref="IWttParticipantModule.PrepareWttParticipantAsync"/>
    public async Task<AZOAResult<WttParticipantPreparation>> PrepareWttParticipantAsync(WttOperationIntent intent,
        WttStageQuote quote, string stage, int generation, CancellationToken ct = default)
    {
        try
        {
            var plan = ReadWttAlgoTargetQuote(intent, WttAlgoQuotedProofs(quote), quote);
            var step = plan.Steps.SingleOrDefault(value => value.Stage == stage);
            if (step is null || stage != "RecipientOptIn" || generation < 0)
                return Error<WttParticipantPreparation>("Only the exact recipient's ASA opt-in is a participant stage.");
            var binding = new WttPreparationBinding(quote.ProtocolIdentity, intent.ActorUserId, intent.TenantId,
                intent.OperationId, "Target", stage, generation, quote.IntentHash, quote.QuoteId);
            using var scope = _custodyScopeFactory?.CreateScope()
                ?? throw new InvalidOperationException("Durable participant preparation storage is required.");
            var store = scope.ServiceProvider.GetRequiredService<IWttPreparationStore>();
            var id = WttPreparationIdentity.Id(binding);
            var row = await store.GetAsync(id, ct);
            if (row is null)
            {
                if (quote.ExpiresAt <= DateTimeOffset.UtcNow)
                    return Error<WttParticipantPreparation>("The unadmitted participant quote expired.");
                row = (await ClaimWttAlgoTargetWireAsync(intent, plan, step, binding, store, null, ct)).Row;
            }
            var wire = ReadWttAlgoTargetWire(row, binding, intent, plan, step);
            if (wire.Transactions.Length != 1) throw new InvalidDataException("Recipient participation must contain exactly one self opt-in.");
            return Ok(new WttParticipantPreparation(intent.OperationId, stage, generation, intent.RecipientAddress,
                quote.ProtocolIdentity, wire.Transactions[0], row.PlanJson, step.Reservation));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or ArgumentException or OverflowException)
        { return Error<WttParticipantPreparation>(exception.Message); }
    }

    /// <inheritdoc cref="IWttParticipantModule.AcceptWttParticipantSignatureAsync"/>
    public async Task<AZOAResult<WttStagePreparation>> AcceptWttParticipantSignatureAsync(WttOperationIntent intent,
        WttStageQuote quote, WttParticipantPreparation prepared, string signedTransactionBase64, CancellationToken ct = default)
    {
        try
        {
            var plan = ReadWttAlgoTargetQuote(intent, WttAlgoQuotedProofs(quote), quote);
            var step = plan.Steps.SingleOrDefault(value => value.Stage == prepared.Stage);
            if (step is null || prepared.Stage != "RecipientOptIn" || prepared.Generation < 0
                || prepared.OperationId != intent.OperationId || prepared.ParticipantAddress != intent.RecipientAddress
                || prepared.ProtocolIdentity != quote.ProtocolIdentity || prepared.Reservation != step.Reservation)
                return Error<WttStagePreparation>("Participant response changed its exact operation, account or cost consent.");
            var binding = new WttPreparationBinding(quote.ProtocolIdentity, intent.ActorUserId, intent.TenantId,
                intent.OperationId, "Target", prepared.Stage, prepared.Generation, quote.IntentHash, quote.QuoteId);
            using var scope = _custodyScopeFactory?.CreateScope()
                ?? throw new InvalidOperationException("Durable participant preparation storage is required.");
            var store = scope.ServiceProvider.GetRequiredService<IWttPreparationStore>();
            var id = WttPreparationIdentity.Id(binding);
            var row = await store.GetAsync(id, ct) ?? throw new InvalidDataException("Participant message was never durably prepared.");
            var wire = ReadWttAlgoTargetWire(row, binding, intent, plan, step);
            if (wire.Transactions.Length != 1 || prepared.UnsignedTransactionBase64 != wire.Transactions[0]
                || prepared.RecoveryMetadata != row.PlanJson)
                throw new InvalidDataException("Participant response differs from its durable original unsigned message.");
            var verified = ValidateWttAlgoTargetEnvelopes(intent, plan, step, wire, id, [signedTransactionBase64]);
            var candidate = new WttStagePreparation(step.Stage, prepared.Generation, verified[0].Tx.TxID(), signedTransactionBase64,
                quote.ProtocolIdentity, row.PlanJson, step.Reservation, "[]");
            if (row.SignedPreparationJson is not null)
            {
                var incumbent = ReadWttAlgoTargetPrepared(row, binding, intent, plan, step);
                return incumbent == candidate ? Ok(incumbent) : Error<WttStagePreparation>("A different exact signed candidate already owns this generation.");
            }
            var saved = await store.TrySaveSignedAsync(row, CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(candidate)), ct)
                ?? await store.GetAsync(id, ct);
            if (saved?.SignedPreparationJson is null || saved.Sealed)
                return Error<WttStagePreparation>("Participant generation changed before signature persistence.");
            return Ok(ReadWttAlgoTargetPrepared(saved, binding, intent, plan, step));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or ArgumentException or OverflowException)
        { return Error<WttStagePreparation>(exception.Message); }
    }
}
