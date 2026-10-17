using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    private sealed record WttAlgoTargetWirePlan(ulong FirstValid, ulong Asset, string[] Transactions);

    /// <inheritdoc cref="IWttTargetModule.PrepareWttTargetAsync"/>
    public async Task<AZOAResult<WttStagePreparation>> PrepareWttTargetAsync(WttOperationIntent intent,
        WttTargetProofs proofs, WttStageQuote quote, string stage, int generation, CancellationToken ct = default)
    {
        try
        {
            var plan = ReadWttAlgoTargetQuote(intent, proofs, quote);
            var step = plan.Steps.SingleOrDefault(value => value.Stage == stage);
            if (generation < 0 || step is null) return Error<WttStagePreparation>("Unknown target stage or generation.");
            var binding = new WttPreparationBinding(quote.ProtocolIdentity, intent.ActorUserId, intent.TenantId,
                intent.OperationId, "Target", stage, generation, quote.IntentHash, quote.QuoteId);
            var id = WttPreparationIdentity.Id(binding);
            using var scope = _custodyScopeFactory?.CreateScope()
                ?? throw new InvalidOperationException("Durable WTT preparation storage is required.");
            var store = scope.ServiceProvider.GetRequiredService<IWttPreparationStore>();
            var row = await store.GetAsync(id, ct);
            if (row?.SignedPreparationJson is not null) return Ok(ReadWttAlgoTargetPrepared(row, binding, intent, plan, step));
            if (row?.Sealed == true || quote.ExpiresAt <= DateTimeOffset.UtcNow)
                return Error<WttStagePreparation>("Target generation was sealed or its unadmitted quote expired.");
            if (stage == "RecipientOptIn" && intent.RecipientAddress != intent.TargetAuthority.Address)
                return Error<WttStagePreparation>("This prerequisite requires the recipient's exact wallet signature.");
            var current = await ClaimWttAlgoTargetWireAsync(intent, plan, step, binding, store, row, ct);
            row = current.Row;
            if (row.SignedPreparationJson is not null) return Ok(ReadWttAlgoTargetPrepared(row, binding, intent, plan, step));
            var winner = ReadWttAlgoTargetWire(row, binding, intent, plan, step);
            if (current.Round < winner.FirstValid || current.Round > checked(winner.FirstValid + 1000)
                || stage is "Redeem" or "RecipientOptIn" && winner.Asset != current.Asset || quote.ExpiresAt <= DateTimeOffset.UtcNow)
                return Error<WttStagePreparation>("Unsigned target generation expired or its registered asset changed.");
            var group = BuildWttAlgoTargetStep(intent, plan, step, winner.FirstValid, id, winner.Asset);
            var batches = stage is "Register" or "Redeem"
                ? ((stage == "Register" ? plan.Attestation! : plan.Transfer).Vaa.Signatures.Count + 5) / 6 : 0;
            var envelopes = new List<string>();
            for (var index = 0; index < group.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (step.StorageApplication != 0 && index == 1)
                    envelopes.Add(Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(WttAlgorandStorageWire.ProtocolEnvelope(
                        WttAlgoTargetStorageParameters(intent, plan, step, winner.FirstValid, id)))));
                else if (index < batches)
                    envelopes.Add(Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(WttAlgorandTargetWire.ProtocolEnvelope(
                        WttAlgoTargetParameters(intent, plan, step, winner.FirstValid, id, winner.Asset), index))));
                else
                {
                    var signed = await SignViaCustodyAsync(Encoder.EncodeToMsgPackOrdered(group[index]),
                        "prepare exact Wormhole target stage", intent.TargetAuthority.Context);
                    if (signed.IsError || signed.Result is null) return Error<WttStagePreparation>(signed.Message);
                    envelopes.Add(Convert.ToBase64String(signed.Result));
                }
            }
            var verified = ValidateWttAlgoTargetEnvelopes(intent, plan, step, winner, id, envelopes);
            var prepared = new WttStagePreparation(stage, generation, verified[0].Tx.TxID(), envelopes[0],
                quote.ProtocolIdentity, row.PlanJson, step.Reservation,
                CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(envelopes.Skip(1).ToArray())));
            var committed = await store.TrySaveSignedAsync(row, CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(prepared)), ct)
                ?? await store.GetAsync(id, ct);
            if (committed?.SignedPreparationJson is null || committed.Sealed)
                return Error<WttStagePreparation>("Target generation changed before signed candidate persistence.");
            return Ok(ReadWttAlgoTargetPrepared(committed, binding, intent, plan, step));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or ArgumentException or OverflowException)
        { return Error<WttStagePreparation>(exception.Message); }
    }

    private static WttTargetProofs WttAlgoQuotedProofs(WttStageQuote quote)
    {
        var plan = JsonSerializer.Deserialize<WttAlgorandTargetPlan>(quote.PlanJson)
            ?? throw new InvalidDataException("Target participant quote is missing.");
        return new(plan.Transfer, plan.Attestation);
    }

    private async Task<(WttPreparation Row, ulong Round, ulong Asset)> ClaimWttAlgoTargetWireAsync(WttOperationIntent intent,
        WttAlgorandTargetPlan plan, WttAlgorandTargetStep step, WttPreparationBinding binding,
        IWttPreparationStore store, WttPreparation? row, CancellationToken ct)
    {
        var current = await RequireWttAlgoTargetReadinessAsync(intent, plan, step, ct);
        if (row is null)
        {
            var id = WttPreparationIdentity.Id(binding);
            var asset = step.Stage is "Redeem" or "RecipientOptIn" ? current.Asset : plan.Asset ?? 0;
            var transactions = BuildWttAlgoTargetStep(intent, plan, step, current.Round, id, asset);
            row = await store.ClaimAsync(new WttPreparation
            {
                Id = id, BindingJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(binding)),
                PlanJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new WttAlgoTargetWirePlan(current.Round, asset,
                    transactions.Select(item => Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(item))).ToArray()))),
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            }, ct);
        }
        return (row, current.Round, current.Asset);
    }

    private static WttAlgoTargetWirePlan ReadWttAlgoTargetWire(WttPreparation row, WttPreparationBinding binding,
        WttOperationIntent intent, WttAlgorandTargetPlan plan, WttAlgorandTargetStep step)
    {
        if (row.Sealed || row.Id != WttPreparationIdentity.Id(binding)
            || row.BindingJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(binding)))
            throw new InvalidDataException("Target generation identity changed or was sealed.");
        var wire = JsonSerializer.Deserialize<WttAlgoTargetWirePlan>(row.PlanJson)
            ?? throw new InvalidDataException("Target unsigned group is missing.");
        if (wire.Transactions is null || wire.FirstValid == 0 || plan.Asset is { } asset && wire.Asset != asset
            || step.Stage == "RecipientOptIn" && wire.Asset == 0
            || step.Stage is not ("RecipientOptIn" or "Redeem") && wire.Asset != (plan.Asset ?? 0)
            || !wire.Transactions.SequenceEqual(BuildWttAlgoTargetStep(intent, plan, step, wire.FirstValid, row.Id, wire.Asset)
                .Select(item => Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(item))))
            || row.PlanJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(wire)))
            throw new InvalidDataException("Target unsigned group differs from its exact immutable intent.");
        return wire;
    }

    private static WttStagePreparation ReadWttAlgoTargetPrepared(WttPreparation row, WttPreparationBinding binding,
        WttOperationIntent intent, WttAlgorandTargetPlan plan, WttAlgorandTargetStep step)
    {
        var wire = ReadWttAlgoTargetWire(row, binding, intent, plan, step);
        var prepared = JsonSerializer.Deserialize<WttStagePreparation>(row.SignedPreparationJson!)
            ?? throw new InvalidDataException("Target signed group is missing.");
        var additional = JsonSerializer.Deserialize<string[]>(prepared.AdditionalSignedTransactionsJson ?? "null")
            ?? throw new InvalidDataException("Target signed group members are missing.");
        var signed = ValidateWttAlgoTargetEnvelopes(intent, plan, step, wire, row.Id, [prepared.SignedTransactionBase64, .. additional]);
        if (prepared.Stage != binding.Stage || prepared.Generation != binding.Generation || prepared.ProtocolIdentity != binding.ProtocolIdentity
            || prepared.RecoveryMetadata != row.PlanJson || prepared.Reservation != step.Reservation || prepared.TransactionId != signed[0].Tx.TxID()
            || prepared.AdditionalSignedTransactionsJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(additional)))
            throw new InvalidDataException("Target signed candidate differs from its saved generation.");
        return prepared;
    }

    private static IReadOnlyList<SignedTransaction> ValidateWttAlgoTargetEnvelopes(WttOperationIntent intent,
        WttAlgorandTargetPlan plan, WttAlgorandTargetStep step, WttAlgoTargetWirePlan wire, string key, IReadOnlyList<string> envelopes)
        => step.StorageApplication != 0
            ? WttAlgorandStorageWire.ValidateSigned(WttAlgoTargetStorageParameters(intent, plan, step, wire.FirstValid, key), envelopes)
            : step.Stage is "Register" or "Redeem"
                ? WttAlgorandTargetWire.ValidateSigned(WttAlgoTargetParameters(intent, plan, step, wire.FirstValid, key, wire.Asset), envelopes)
                : WttAlgorandGroup.ValidateSingleOwner(BuildWttAlgoTargetStep(intent, plan, step, wire.FirstValid, key, wire.Asset), envelopes, intent.RecipientAddress);
}
