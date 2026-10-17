using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using Event = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentWebhookEvent;
using Registration = AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookRegistration;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed partial class SurrealConsentWebhookDeliveryStore(ISurrealExecutor executor) : IConsentWebhookDeliveryStore
{
    private const string Conflict = "WEBHOOK_DELIVERY_CHANGED";

    /// <inheritdoc/>
    public Task<IReadOnlyList<Event>> ListCandidatesAsync(int limit, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return executor.QueryAsync<Event>(SurrealQuery<Event>.From()
            .Where(row => row.Status == Event.DeliveryStatus.Pending && row.NextAttemptAt <= now
                && (row.LeaseExpiresAt == null || row.LeaseExpiresAt <= now))
            .OrderBy(row => row.NextAttemptAt).Limit(Math.Clamp(limit, 1, 1000)), ct);
    }

    /// <inheritdoc/>
    public async Task<WebhookDeliveryLease?> TryClaimAsync(string eventId, long expectedVersion, TimeSpan duration,
        int maximumAttempts, CancellationToken ct = default)
    {
        if (expectedVersion < 0 || expectedVersion == long.MaxValue || duration < TimeSpan.FromSeconds(5)
            || duration > TimeSpan.FromMinutes(5) || maximumAttempts is < 1 or > 100) return null;
        var evt = await executor.QuerySingleAsync<Event>(SurrealQuery<Event>.Key(eventId), ct);
        var now = DateTimeOffset.UtcNow;
        if (evt is null || evt.Version != expectedVersion || evt.Status != Event.DeliveryStatus.Pending
            || evt.NextAttemptAt > now || evt.LeaseExpiresAt > now || evt.CycleAttemptCount >= maximumAttempts
            || evt.AttemptCount is < 0 or long.MaxValue || evt.CycleAttemptCount < 0
            || evt.TransitionId is null || evt.RegistrationId is null || evt.BodyJson is null) return null;
        if (!await HasEvidenceAsync(evt, ct)) return null;
        var registration = await executor.QuerySingleAsync<Registration>(SurrealQuery<Registration>.Key(evt.RegistrationId), ct);
        if (registration is null || !registration.IsActive || registration.TenantId != evt.TenantId
            || registration.DestinationRevision != evt.DestinationRevision || registration.Url != evt.DestinationUrl
            || registration.SecretRevision <= 0 || string.IsNullOrEmpty(registration.EncryptedSecret)) return null;
        var id = Guid.NewGuid().ToString("N");
        var expiry = now + duration;
        var lease = new WebhookDeliveryLease(eventId, expectedVersion + 1, id, expiry,
            SurrealRecordGuid.Parse(evt.TenantId), SurrealRecordGuid.Parse(evt.RegistrationId), registration.DestinationRevision,
            registration.Url, evt.BodyJson, evt.IdempotencyId, evt.AttemptCount + 1,
            registration.SecretRevision, registration.EncryptedSecret, evt.CycleAttemptCount + 1);
        // raw: current destination/secret authorization and exact event lease are one serialized multi-record transition.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Registration>(evt.RegistrationId)
            .Where(row => row.Version == registration.Version && row.IsActive && row.TenantId == evt.TenantId
                && row.DestinationRevision == registration.DestinationRevision && row.Url == registration.Url
                && row.SecretRevision == registration.SecretRevision && row.EncryptedSecret == registration.EncryptedSecret)
            .Set(row => row.Version, registration.Version).Build(), "delivery_registration", "_delivery_registration"));
        statements.Add(SurrealQuery.Of("IF $_delivery_registration = NONE OR $_delivery_registration = [] { THROW 'WEBHOOK_DELIVERY_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.AssignRecord(SurrealQuery<Event>.Key(eventId), "delivery_read", "_delivery_before"));
        statements.Add(SurrealQuery.Of("""
            IF $_delivery_before = NONE OR $_delivery_before.next_attempt_at > time::now()
                OR ($_delivery_before.lease_expires_at != NONE AND $_delivery_before.lease_expires_at > time::now())
                OR $_delivery_expiry <= time::now() { THROW 'WEBHOOK_DELIVERY_CHANGED' }
            """).WithParam("_delivery_expiry", expiry));
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Event>(eventId)
            .Where(row => row.Version == expectedVersion && row.Status == Event.DeliveryStatus.Pending
                && row.AttemptCount == evt.AttemptCount && row.CycleAttemptCount == evt.CycleAttemptCount
                && row.TransitionId == evt.TransitionId && row.TenantId == evt.TenantId
                && row.BodyHash == evt.BodyHash && row.IdempotencyId == evt.IdempotencyId
                && row.RegistrationId == evt.RegistrationId && row.DestinationRevision == evt.DestinationRevision
                && row.DestinationUrl == evt.DestinationUrl && row.BodyJson == evt.BodyJson)
            .Set(row => row.Version, lease.Version).Set(row => row.LeaseId, id).Set(row => row.LeaseExpiresAt, expiry)
            .Set(row => row.AttemptCount, lease.AttemptCount).Set(row => row.AttemptSecretRevision, lease.SecretRevision)
            .Set(row => row.CycleAttemptCount, lease.CycleAttemptCount)
            .Set(row => row.AttemptEncryptedSecret, lease.EncryptedSecret).Build(), "delivery_claim", "_delivery_saved"));
        statements.Add(SurrealQuery.Of("IF $_delivery_saved = NONE OR $_delivery_saved = [] { THROW 'WEBHOOK_DELIVERY_CHANGED' }"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        return await CommitAsync(SurrealQuery.Combine(statements.ToArray()), ct) ? lease : null;
    }

    /// <inheritdoc/>
    public async Task<bool> FinishAsync(WebhookDeliveryLease lease, WebhookAttemptOutcome outcome, TimeSpan retryDelay,
        string? errorCode, CancellationToken ct = default)
    {
        if (lease.Version is < 1 or long.MaxValue || !Enum.IsDefined(outcome) || retryDelay < TimeSpan.Zero
            || retryDelay > TimeSpan.FromDays(1) || (errorCode is not null && (errorCode.Length > 128
                || errorCode.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')))) return false;
        var status = outcome switch { WebhookAttemptOutcome.Delivered => Event.DeliveryStatus.Delivered,
            WebhookAttemptOutcome.DeadLetter => Event.DeliveryStatus.DeadLettered, _ => Event.DeliveryStatus.Pending };
        var mutation = SurrealWriter.UpdateOnly<Event>(lease.EventId)
            .Where(row => row.Version == lease.Version && row.Status == Event.DeliveryStatus.Pending
                && row.LeaseId == lease.LeaseId && row.LeaseExpiresAt == lease.ExpiresAt)
            .Set(row => row.Version, lease.Version + 1).Set(row => row.Status, status)
            .Set(row => row.NextAttemptAt, DateTimeOffset.UtcNow + retryDelay)
            .Unset(row => row.LeaseId).Unset(row => row.LeaseExpiresAt)
            .Unset(row => row.AttemptSecretRevision).Unset(row => row.AttemptEncryptedSecret);
        mutation = outcome == WebhookAttemptOutcome.Delivered || errorCode is null
            ? mutation.Unset(row => row.LastError) : mutation.Set(row => row.LastError, errorCode);
        // raw: lease expiry and conditional completion share the same database-time transaction.
        var query = SurrealQuery.Combine(SurrealQuery.Of("BEGIN"),
            SurrealQuery.Of("IF $_delivery_expiry <= time::now() { THROW 'WEBHOOK_DELIVERY_CHANGED' }").WithParam("_delivery_expiry", lease.ExpiresAt),
            SurrealGeneratedStatementScope.Assign(mutation.Build(), "delivery_finish", "_delivery_finished"),
            SurrealQuery.Of("IF $_delivery_finished = NONE OR $_delivery_finished = [] { THROW 'WEBHOOK_DELIVERY_CHANGED' }"),
            SurrealQuery.Of("COMMIT"));
        return await CommitAsync(query, ct);
    }

    private async Task<bool> CommitAsync(SurrealQuery query, CancellationToken ct)
    {
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct);
                response.EnsureAllOk();
                return response;
            }, ct);
            return true;
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(Conflict, StringComparison.Ordinal))
        {
            return false;
        }
    }
}
