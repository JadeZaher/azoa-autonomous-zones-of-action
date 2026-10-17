using System.Text.Json;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Wormhole;

public sealed partial class WttLifecycleService
{
    private static WttOfferView OfferView(WttOffer offer, WttOperationIntent intent, WttStageQuote quote, WttTargetInspection target)
        => new(offer.Id, offer.OfferHash, offer.OperationId, intent,
            new(quote.QuoteId, quote.ProtocolIdentity, quote.Phase, quote.Stages, quote.ExpiresAt), target, offer.ExpiresAt,
            offer.ReturnOfOperationId);

    private async Task<WttOperationView> ProjectAsync(WttOperation operation, CancellationToken ct)
    {
        var id = SurrealRecordGuid.BareId(operation.Id);
        var stages = await store.ReadStagesAsync(id, ct);
        var current = operation.CurrentStageId is { } currentId ? stages.LastOrDefault(stage => SurrealRecordGuid.BareId(stage.Id) == currentId) : null;
        var (publicState, nextAction) = Status(operation, current);
        return new(id, operation.OfferId, operation.Version, operation.State, publicState, operation.Phase, Intent(operation),
            stages.Select(StageView).ToArray(), operation.CurrentStageId,
            operation.TransferPublicationJson is { } transfer ? Read<WttPublication>(transfer).Sequence : null,
            operation.AttestationPublicationJson is { } attestation ? Read<WttPublication>(attestation).Sequence : null,
            operation.ProofsJson is not null, nextAction, operation.Detail,
            operation.SettlementJson is { } settlement ? Read<WttSettlement>(settlement) : null, operation.ReturnOfOperationId,
            operation.CreatedAt, operation.UpdatedAt);
    }

    internal static (string PublicState, string NextAction) Status(WttOperation operation, WttOperationStage? current)
    {
        var target = operation.Phase is "Target" or "NativeOutput";
        var stageAction = current is null ? "None" : current.State switch
        {
            "Planned" when current.ParticipantAddress is not null => "SignParticipant",
            "Exposed" when current.PreparedJson is null => "SignParticipant",
            "Planned" or "Preparing" => "Continue",
            "Prepared" or "Exposed" => "Submit",
            "SubmissionAuthorized" => "AwaitFinality",
            _ => "None",
        };
        if (operation.State == "Active" && current?.Phase == "Cleanup")
            return (operation.ResumeState == "Settled" ? "Settled" : target ? "AwaitingTargetAuthorization" : "AwaitingAttestation", stageAction);
        return operation.State switch
        {
            "Settled" => ("Settled", "None"),
            "Cancelled" => ("CancelledBeforeEffect", "None"),
            "NeedsOperator" => ("NeedsOperator", "Operator"),
            "AwaitingProof" => ("AwaitingAttestation", "AwaitProof"),
            "Active" when current is not null => current.State switch
            {
                "Planned" when current.ParticipantAddress is not null => ("AwaitingTargetParticipant", stageAction),
                "Exposed" when current.PreparedJson is null => ("AwaitingTargetParticipant", stageAction),
                "Planned" or "Preparing" or "Prepared" or "Exposed" => (target ? "TargetPrepared" : "SourcePrepared", stageAction),
                _ => (target ? "TargetSubmitted" : "SourceSubmitted", stageAction),
            },
            _ when operation.Phase == "NativeOutput" => ("AwaitingTargetParticipant", "StartNativeOutput"),
            _ when operation.Phase == "Complete" => ("Settled", "None"),
            _ when target => ("AwaitingTargetAuthorization", "Continue"),
            "Admitted" => ("Admitted", "Continue"),
            _ => ("AwaitingSourceAuthorization", "Continue"),
        };
    }

    private static WttStageView StageView(WttOperationStage stage)
    {
        var prepared = stage.PreparedJson is null ? null : Read<WttStagePreparation>(stage.PreparedJson);
        var observation = Observation(stage);
        return new(SurrealRecordGuid.BareId(stage.Id), stage.Phase, stage.Stage, stage.Generation, stage.Version, stage.State,
            prepared?.Reservation ?? QuotedReservation(stage), observation?.TransactionId ?? prepared?.TransactionId,
            observation?.NetworkFeePaid, observation?.Detail, stage.AuthorizedAt is not null, stage.ExposedAt is not null,
            stage.ParticipantAddress);
    }

    internal static WttStageObservation? Observation(WttOperationStage stage)
    {
        if (stage.ObservationJson is null) return null;
        return stage.Phase switch
        {
            "NativeOutput" => Read<WttNativeOutputObservation>(stage.ObservationJson).Stage,
            "Cleanup" => Read<WttCleanupObservation>(stage.ObservationJson).Stage,
            _ => Read<WttStageObservation>(stage.ObservationJson),
        };
    }

    private static WttStageReservation? QuotedReservation(WttOperationStage stage)
    {
        switch (stage.Phase)
        {
            case "NativeOutput":
                var native = Read<WttNativeOutputQuote>(stage.QuoteJson);
                return new(stage.Stage, native.NetworkFee, native.TemporaryRent, "0", native.RecipientAddress);
            case "Cleanup":
                var cleanup = Read<WttCleanupQuote>(stage.QuoteJson);
                return new(stage.Stage, cleanup.NetworkFee, "0", "0", cleanup.DestinationAddress);
            default:
                return Read<WttStageQuote>(stage.QuoteJson).Stages.FirstOrDefault(value => value.Stage == stage.Stage);
        }
    }

    internal static WttOperationIntent Intent(WttOperation operation) => Read<WttOperationIntent>(operation.IntentJson);

    internal static T Read<T>(string json)
        => JsonSerializer.Deserialize<T>(json) ?? throw new InvalidOperationException("Persisted WTT journal evidence is missing.");
}
