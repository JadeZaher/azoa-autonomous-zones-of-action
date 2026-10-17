using AZOA.WebAPI.Core.Surreal;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Event = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentWebhookEvent;
using Registration = AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookRegistration;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealConsentWebhookDeliveryStore
{
    /// <inheritdoc/>
    public async Task<bool> SuspendUnavailableAsync(string eventId, long expectedVersion, int maximumAttempts, CancellationToken ct = default)
    {
        if (expectedVersion < 0 || expectedVersion == long.MaxValue || maximumAttempts is < 1 or > 100) return false;
        var evt = await executor.QuerySingleAsync<Event>(SurrealQuery<Event>.Key(eventId), ct);
        if (evt is null || evt.Version != expectedVersion || evt.Status != Event.DeliveryStatus.Pending) return false;
        string? reason = evt.CycleAttemptCount >= maximumAttempts ? "ATTEMPTS_EXHAUSTED" : null;
        if (reason is null)
        {
            if (!await HasEvidenceAsync(evt, ct))
                reason = "ACCEPTANCE_EVIDENCE_MISSING";
        }
        if (reason is null)
        {
            var registration = evt.RegistrationId is null ? null
                : await executor.QuerySingleAsync<Registration>(SurrealQuery<Registration>.Key(evt.RegistrationId), ct);
            if (registration is null || !registration.IsActive || registration.TenantId != evt.TenantId
                || registration.DestinationRevision != evt.DestinationRevision || registration.Url != evt.DestinationUrl
                || registration.SecretRevision <= 0 || string.IsNullOrEmpty(registration.EncryptedSecret))
                reason = "ORIGINAL_DESTINATION_UNAVAILABLE";
        }
        if (reason is null) return false;
        // raw: unavailable evidence cannot retire an active or newly acquired event lease.
        var query = SurrealQuery.Combine(SurrealQuery.Of("BEGIN"),
            SurrealGeneratedStatementScope.Assign(SurrealQuery<Event>.Key(eventId), "delivery_unavailable_read", "_delivery_before"),
            SurrealQuery.Of("""
                IF $_delivery_before = NONE OR $_delivery_before.next_attempt_at > time::now()
                    OR ($_delivery_before.lease_expires_at != NONE AND $_delivery_before.lease_expires_at > time::now())
                    { THROW 'WEBHOOK_DELIVERY_CHANGED' }
                """),
            SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Event>(eventId)
                .Where(row => row.Version == expectedVersion && row.Status == Event.DeliveryStatus.Pending)
                .Set(row => row.Status, Event.DeliveryStatus.DeadLettered).Set(row => row.Version, expectedVersion + 1)
                .Set(row => row.LastError, reason).Unset(row => row.LeaseId).Unset(row => row.LeaseExpiresAt)
                .Unset(row => row.AttemptSecretRevision).Unset(row => row.AttemptEncryptedSecret).Build(),
                "delivery_unavailable", "_delivery_saved"),
            SurrealQuery.Of("IF $_delivery_saved = NONE OR $_delivery_saved = [] { THROW 'WEBHOOK_DELIVERY_CHANGED' }"),
            SurrealQuery.Of("COMMIT"));
        return await CommitAsync(query, ct);
    }
}
