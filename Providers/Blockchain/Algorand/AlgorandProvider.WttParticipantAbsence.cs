using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    /// <inheritdoc cref="IWttParticipantModule.ObserveExposedWttParticipantAsync"/>
    public async Task<AZOAResult<WttExposedObservation>> ObserveExposedWttParticipantAsync(WttOperationIntent intent,
        WttStageQuote quote, WttParticipantPreparation participant, string? previousEvidenceJson, CancellationToken ct = default)
    {
        try
        {
            var plan = ReadWttAlgoTargetQuote(intent, WttAlgoQuotedProofs(quote), quote);
            var step = plan.Steps.SingleOrDefault(value => value.Stage == participant.Stage);
            if (step is null || participant.Stage != "RecipientOptIn" || participant.OperationId != intent.OperationId
                || participant.ParticipantAddress != intent.RecipientAddress || participant.ProtocolIdentity != quote.ProtocolIdentity)
                return Error<WttExposedObservation>("Only the exact disclosed recipient opt-in can be observed as a participant message.");
            var binding = new WttPreparationBinding(quote.ProtocolIdentity, intent.ActorUserId, intent.TenantId,
                intent.OperationId, "Target", participant.Stage, participant.Generation, quote.IntentHash, quote.QuoteId);
            using var scope = _custodyScopeFactory?.CreateScope()
                ?? throw new InvalidOperationException("Durable participant preparation storage is required.");
            var row = await scope.ServiceProvider.GetRequiredService<IWttPreparationStore>().GetAsync(WttPreparationIdentity.Id(binding), ct)
                ?? throw new InvalidDataException("Participant message was never durably prepared.");
            var wire = ReadWttAlgoTargetWire(row, binding, intent, plan, step);
            if (wire.Transactions.Length != 1 || wire.Transactions[0] != participant.UnsignedTransactionBase64 || participant.RecoveryMetadata != row.PlanJson)
                throw new InvalidDataException("Observed participant message differs from its durable original.");
            var expected = BuildWttAlgoTargetStep(intent, plan, step, wire.FirstValid, row.Id, wire.Asset);
            var transactionId = expected[0].TxID();
            var parameters = await ReadWttAlgorandJsonAsync("/v2/transactions/params", ct);
            var head = WttAlgoUnsigned(parameters, "last-round");
            if (head == 0 || WttAlgoText(parameters, "genesis-hash") != plan.GenesisHash || WttAlgoText(parameters, "genesis-id") != plan.GenesisId)
                return Error<WttExposedObservation>("Participant observation endpoint differs from its saved physical network.");
            var pending = new WttExposedObservation("Pending", Detail: "The disclosed opt-in is neither landed nor provably expired.");
            var indexed = await ReadWttAlgoIndexedTransactionAsync(transactionId, ct);
            if (indexed is null) return Ok(pending);
            if (indexed.Value.Transaction is { } landed)
            {
                // The participant broadcast its own signature; recover and validate it through the ordinary acceptance path.
                if (RecoverWttAlgoSingleSignature(landed, expected[0]) is not { } signed)
                    return Ok(pending with { Detail = "The landed opt-in's exact single signature is not yet recoverable." });
                var accepted = await AcceptWttParticipantSignatureAsync(intent, quote, participant, signed, ct);
                return accepted.IsError || accepted.Result is null ? Ok(pending with { Detail = accepted.Message })
                    : Ok(new WttExposedObservation("Landed", Landed: accepted.Result, Detail: "The participant submitted the disclosed opt-in."));
            }
            var stub = new WttStagePreparation(participant.Stage, participant.Generation, transactionId, "", quote.ProtocolIdentity,
                row.PlanJson, participant.Reservation);
            var absence = await ObserveWttAlgoAbsenceAsync(intent, quote, stub, plan.GenesisHash, wire.FirstValid, expected, head,
                indexed.Value.Round, new("Pending", transactionId, quote.ProtocolIdentity), ct);
            return absence.IsError || absence.Result is not { Verdict: "ExpiredNoEffect" } proof ? Ok(pending)
                : Ok(new WttExposedObservation("ExpiredNoEffect", proof.EvidenceJson, Detail: "The opt-in expired unsigned past its last valid round."));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or ArgumentException or OverflowException)
        { return Error<WttExposedObservation>(exception.Message); }
    }

    /// <summary>Rebuilds the exact single-key signed envelope from an indexed transaction, or null when it is not exactly that.</summary>
    private static string? RecoverWttAlgoSingleSignature(JsonElement indexed, Transaction expected)
    {
        if (!indexed.TryGetProperty("signature", out var signature) || signature.ValueKind != JsonValueKind.Object
            || signature.EnumerateObject().Any(property => property.Name != "sig"
                && (property.Name is not ("multisig" or "logicsig") || property.Value.ValueKind != JsonValueKind.Null))
            || !signature.TryGetProperty("sig", out var encoded) || encoded.ValueKind != JsonValueKind.String
            || indexed.TryGetProperty("auth-addr", out var authorization) && authorization.ValueKind != JsonValueKind.Null)
            return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded.GetString()!); }
        catch (FormatException) { return null; }
        return bytes.Length != 64 || Convert.ToBase64String(bytes) != encoded.GetString() ? null
            : Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(new SignedTransaction(expected, bytes, null, null, null)));
    }
}
