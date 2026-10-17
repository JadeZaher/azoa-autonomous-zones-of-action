using System.Text.Json;
using AZOA.WebAPI.Models;
using Event = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentWebhookEvent;
using Transition = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentLifecycleTransition;

namespace AZOA.WebAPI.Helpers;

/// <summary>Matches a frozen notification to its immutable consent transition evidence.</summary>
public static class ConsentNotificationEvidence
{
    public static bool Matches(Event notification, Transition proof)
    {
        if (string.IsNullOrWhiteSpace(proof.Id) || string.IsNullOrWhiteSpace(notification.Id)
            || !Enum.TryParse<ConsentWebhookEventType>(proof.Kind, out var kind) || !Enum.IsDefined(kind)
            || proof.Kind != notification.EventType.ToString() || string.IsNullOrWhiteSpace(proof.AuthorityJson)) return false;
        ConsentLifecycleReceipt? receipt;
        try { receipt = JsonSerializer.Deserialize<ConsentLifecycleReceipt>(proof.GrantReceiptJson); }
        catch (JsonException) { return false; }
        if (receipt is null || receipt.Scopes is null || receipt.Scopes.Any(string.IsNullOrWhiteSpace)
            || receipt.GrantId == Guid.Empty || receipt.GrantorUserId == Guid.Empty || receipt.TenantId == Guid.Empty
            || proof.OccurredAt < receipt.GrantedAt
            || (kind == ConsentWebhookEventType.Granted && proof.OccurredAt != receipt.GrantedAt)
            || (kind == ConsentWebhookEventType.Expired && proof.OccurredAt != receipt.ExpiresAt)) return false;
        var frozen = ConsentLifecycleIdentity.Transition(receipt, kind, proof.OccurredAt);
        return SurrealRecordGuid.BareId(proof.Id) == frozen.Id && notification.TransitionId == frozen.Id
            && SurrealRecordGuid.BareId(notification.Id) == frozen.Id && proof.EventId == frozen.Id
            && proof.GrantId == receipt.GrantId.ToString("N")
            && SurrealRecordGuid.ParseOptional(notification.GrantId) == receipt.GrantId
            && SurrealRecordGuid.ParseOptional(notification.TenantId) == receipt.TenantId
            && SurrealRecordGuid.ParseOptional(notification.UserId) == receipt.GrantorUserId
            && notification.Scopes == string.Join(',', receipt.Scopes)
            && notification.ParticipationRef == receipt.ParticipationRef && notification.OccurredAt == frozen.OccurredAt
            && notification.IdempotencyId == frozen.Id && notification.BodyJson == frozen.BodyJson
            && notification.BodyHash == frozen.BodyHash && proof.BodyHash == frozen.BodyHash
            && notification.RegistrationId == proof.RegistrationId
            && notification.DestinationRevision == proof.DestinationRevision && notification.DestinationUrl == proof.DestinationUrl;
    }
}
