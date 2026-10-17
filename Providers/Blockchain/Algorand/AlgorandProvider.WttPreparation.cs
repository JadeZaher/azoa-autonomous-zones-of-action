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
    private sealed record WttAlgoWirePlan(ulong FirstValid, string[] Transactions);

    /// <inheritdoc cref="IWttSourceModule.PrepareWttSourceAsync"/>
    public async Task<AZOAResult<WttStagePreparation>> PrepareWttSourceAsync(WttOperationIntent intent,
        WttStageQuote quote, string stage, int generation, CancellationToken ct = default)
    {
        try
        {
            var plan = ReadWttAlgoSourceQuote(intent, quote);
            var step = plan.Steps.SingleOrDefault(item => item.Stage == stage);
            if (generation < 0 || step is null) return Error<WttStagePreparation>("Unknown WTT source stage or generation.");
            var binding = new WttPreparationBinding(quote.ProtocolIdentity, intent.ActorUserId, intent.TenantId,
                intent.OperationId, "Source", stage, generation, quote.IntentHash, quote.QuoteId);
            var id = WttPreparationIdentity.Id(binding);
            using var scope = _custodyScopeFactory?.CreateScope()
                ?? throw new InvalidOperationException("Durable WTT preparation storage is required.");
            var store = scope.ServiceProvider.GetRequiredService<IWttPreparationStore>();
            var row = await store.GetAsync(id, ct);
            if (row?.SignedPreparationJson is not null)
                return Ok(ReadWttAlgoPrepared(row, binding, intent, plan, step));
            if (row?.Sealed == true || quote.ExpiresAt <= DateTimeOffset.UtcNow)
                return Error<WttStagePreparation>("WTT generation was sealed or its unadmitted quote expired.");
            var fresh = await QuoteWttSourceAsync(intent, ct);
            if (fresh.IsError || fresh.Result is null) return Error<WttStagePreparation>(fresh.Message);
            var current = ReadWttAlgoSourceQuote(intent, fresh.Result);
            var selected = current.Steps.SingleOrDefault(item => item.Stage == stage);
            if (selected != step || CanonicalJson.Hash(current with { Steps = plan.Steps }) != CanonicalJson.Hash(plan)
                || current.Steps.TakeWhile(item => item.Stage != stage).Any(item => item.Stage != "Attest"))
                return Error<WttStagePreparation>("WTT source prerequisites, costs or protocol state changed before preparation.");
            var parameters = await ReadWttAlgorandJsonAsync("/v2/transactions/params", ct);
            var round = WttAlgoUnsigned(parameters, "last-round");
            if (WttAlgoText(parameters, "genesis-hash") != plan.GenesisHash || WttAlgoText(parameters, "genesis-id") != plan.GenesisId
                || WttAlgoUnsigned(parameters, "min-fee") != plan.MinimumFee
                || CanonicalJson.Hash(PriceWttAlgoSourcePlan(intent, plan, WttAlgoUnsigned(parameters, "fee"), round)) != CanonicalJson.Hash(plan))
                return Error<WttStagePreparation>("Current Algorand fees or physical network exceed the saved quote.");
            if (row is null)
            {
                var transactions = BuildWttAlgoSourceStep(intent, plan, step, round, id);
                row = await store.ClaimAsync(new WttPreparation
                {
                    Id = id, BindingJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(binding)),
                    PlanJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new WttAlgoWirePlan(round,
                        transactions.Select(item => Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(item))).ToArray()))),
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                }, ct);
            }
            if (row.SignedPreparationJson is not null) return Ok(ReadWttAlgoPrepared(row, binding, intent, plan, step));
            var winner = ReadWttAlgoWire(row, binding, intent, plan, step);
            if (round < winner.FirstValid || round > checked(winner.FirstValid + 1000) || quote.ExpiresAt <= DateTimeOffset.UtcNow)
                return Error<WttStagePreparation>("Unsigned WTT generation expired and requires journal retirement.");
            var group = BuildWttAlgoSourceStep(intent, plan, step, winner.FirstValid, id);
            var envelopes = new List<string>();
            for (var index = 0; index < group.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (index == 1 && stage is ("EmitterStorage" or "CustodyStorage"))
                    envelopes.Add(Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(WttAlgorandStorageWire.ProtocolEnvelope(
                        WttAlgoStorageParameters(intent, plan, step, winner.FirstValid, id)))));
                else
                {
                    var signed = await SignViaCustodyAsync(Encoder.EncodeToMsgPackOrdered(group[index]),
                        "prepare exact Wormhole source stage", intent.SourceAuthority.Context);
                    if (signed.IsError || signed.Result is null) return Error<WttStagePreparation>(signed.Message);
                    envelopes.Add(Convert.ToBase64String(signed.Result));
                }
            }
            var verified = ValidateWttAlgoEnvelopes(intent, plan, step, winner.FirstValid, id, envelopes);
            var prepared = new WttStagePreparation(stage, generation, verified[0].Tx.TxID(), envelopes[0],
                quote.ProtocolIdentity, row.PlanJson, step.Reservation,
                CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(envelopes.Skip(1).ToArray())));
            var committed = await store.TrySaveSignedAsync(row, CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(prepared)), ct)
                ?? await store.GetAsync(id, ct);
            if (committed?.SignedPreparationJson is null || committed.Sealed)
                return Error<WttStagePreparation>("WTT generation changed before signed candidate persistence.");
            return Ok(ReadWttAlgoPrepared(committed, binding, intent, plan, step));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or ArgumentException or OverflowException)
        { return Error<WttStagePreparation>(exception.Message); }
    }

    private static WttAlgoWirePlan ReadWttAlgoWire(WttPreparation row, WttPreparationBinding binding,
        WttOperationIntent intent, WttAlgorandSourcePlan plan, WttAlgorandSourceStep step)
    {
        if (row.Sealed || row.Id != WttPreparationIdentity.Id(binding)
            || row.BindingJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(binding)))
            throw new InvalidDataException("WTT generation identity changed or was sealed.");
        var wire = JsonSerializer.Deserialize<WttAlgoWirePlan>(row.PlanJson)
            ?? throw new InvalidDataException("WTT saved unsigned group is missing.");
        if (wire.Transactions is null || !wire.Transactions.SequenceEqual(BuildWttAlgoSourceStep(intent, plan, step, wire.FirstValid, row.Id)
            .Select(item => Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(item))))
            || row.PlanJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(wire)))
            throw new InvalidDataException("WTT unsigned group differs from its exact immutable intent.");
        return wire;
    }

    private static WttStagePreparation ReadWttAlgoPrepared(WttPreparation row, WttPreparationBinding binding,
        WttOperationIntent intent, WttAlgorandSourcePlan plan, WttAlgorandSourceStep step)
    {
        var wire = ReadWttAlgoWire(row, binding, intent, plan, step);
        var prepared = JsonSerializer.Deserialize<WttStagePreparation>(row.SignedPreparationJson!)
            ?? throw new InvalidDataException("WTT signed group is missing.");
        var additional = JsonSerializer.Deserialize<string[]>(prepared.AdditionalSignedTransactionsJson ?? "null")
            ?? throw new InvalidDataException("WTT signed group members are missing.");
        var signed = ValidateWttAlgoEnvelopes(intent, plan, step, wire.FirstValid, row.Id, [prepared.SignedTransactionBase64, .. additional]);
        if (prepared.Stage != binding.Stage || prepared.Generation != binding.Generation || prepared.ProtocolIdentity != binding.ProtocolIdentity
            || prepared.RecoveryMetadata != row.PlanJson || prepared.Reservation != step.Reservation || prepared.TransactionId != signed[0].Tx.TxID()
            || prepared.AdditionalSignedTransactionsJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(additional)))
            throw new InvalidDataException("WTT signed candidate differs from its saved generation.");
        return prepared;
    }

    private static IReadOnlyList<SignedTransaction> ValidateWttAlgoEnvelopes(WttOperationIntent intent,
        WttAlgorandSourcePlan plan, WttAlgorandSourceStep step, ulong round, string key, IReadOnlyList<string> envelopes)
        => step.Stage is "EmitterStorage" or "CustodyStorage"
            ? WttAlgorandStorageWire.ValidateSigned(WttAlgoStorageParameters(intent, plan, step, round, key), envelopes)
            : WttAlgorandGroup.ValidateSingleOwner(BuildWttAlgoSourceStep(intent, plan, step, round, key), envelopes, intent.SourceAuthority.Address);
}
