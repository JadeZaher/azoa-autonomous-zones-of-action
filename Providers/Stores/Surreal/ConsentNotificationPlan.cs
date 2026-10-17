using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Audit = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentAudit;
using Event = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentWebhookEvent;
using Registration = AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookRegistration;
using User = AZOA.WebAPI.Persistence.SurrealDb.Models.User;
using Transition = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentLifecycleTransition;
using System.Text.Json;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Frozen notification and destination witnesses joined to a consent lifecycle transaction.</summary>
internal sealed record ConsentNotificationPlan(Event Event, Audit Audit, IReadOnlyList<SurrealQuery> DestinationFence)
{
    public Transition Evidence(ConsentLifecycleReceipt receipt, string authorityJson) => new()
    {
        Id = Event.TransitionId!, GrantId = receipt.GrantId.ToString("N"), Kind = Event.EventType.ToString(),
        GrantReceiptJson = JsonSerializer.Serialize(receipt), AuthorityJson = authorityJson,
        EventId = Event.Id, BodyHash = Event.BodyHash!, OccurredAt = Event.OccurredAt,
        RegistrationId = Event.RegistrationId, DestinationRevision = Event.DestinationRevision,
        DestinationUrl = Event.DestinationUrl,
    };

    public ConsentNotificationPlan ForTransition(ConsentLifecycleReceipt receipt, ConsentWebhookEventType type,
        DateTimeOffset occurredAt, string detail)
    {
        if (SurrealRecordGuid.Parse(Event.TenantId) != receipt.TenantId)
            throw new InvalidDataException("A notification destination cannot be reused across tenants.");
        var transition = ConsentLifecycleIdentity.Transition(receipt, type, occurredAt);
        var actor = SurrealLink.ToLink("user", receipt.GrantorUserId.ToString("N"))!;
        var notification = new Event { Id = transition.Id, TransitionId = transition.Id, TenantId = Event.TenantId,
            GrantId = SurrealLink.ToLink("consent_grant", receipt.GrantId.ToString("N"))!, UserId = actor,
            Scopes = string.Join(',', receipt.Scopes), ParticipationRef = receipt.ParticipationRef,
            EventType = Enum.Parse<Event.WebhookEventType>(type.ToString()), OccurredAt = transition.OccurredAt,
            CreatedAt = Event.CreatedAt, NextAttemptAt = Event.NextAttemptAt, IdempotencyId = transition.Id,
            BodyJson = transition.BodyJson, BodyHash = transition.BodyHash, RegistrationId = Event.RegistrationId,
            DestinationRevision = Event.DestinationRevision, DestinationUrl = Event.DestinationUrl,
            Status = Event.Status, LastError = Event.LastError };
        var audit = new Audit { Id = transition.Id, Action = type.ToString(), GrantId = receipt.GrantId.ToString("N"),
            TenantId = Event.TenantId, UserId = actor, Scope = notification.Scopes, Detail = detail, OccurredAt = transition.OccurredAt };
        return new(notification, audit, DestinationFence);
    }

    public static async Task<ConsentNotificationPlan> PrepareAsync(ISurrealExecutor executor, ConsentLifecycleReceipt receipt,
        ConsentWebhookEventType type, DateTimeOffset occurredAt, string detail, CancellationToken ct, string scope = "consent")
    {
        var tenant = SurrealLink.ToLink("user", receipt.TenantId.ToString("N"))!;
        var tenantRow = await executor.QuerySingleAsync<User>(SurrealQuery<User>.Key(receipt.TenantId.ToString("N")), ct);
        var registrations = await executor.QueryAsync<Registration>(SurrealQuery<Registration>.From()
            .Where(row => row.TenantId == tenant).Limit(2), ct);
        if (registrations.Count > 1) throw new InvalidDataException("Tenant has ambiguous webhook registrations.");
        var registration = registrations.SingleOrDefault();
        var deliverable = tenantRow is { IsActive: true } && registration is { IsActive: true, DestinationRevision: > 0, SecretRevision: > 0 }
            && !string.IsNullOrWhiteSpace(registration.EncryptedSecret);
        var now = DateTimeOffset.UtcNow;
        var notification = new Event { TenantId = tenant, CreatedAt = now, NextAttemptAt = now,
            RegistrationId = registration is null ? null : SurrealRecordGuid.BareId(registration.Id),
            DestinationRevision = registration?.DestinationRevision, DestinationUrl = registration?.Url,
            Status = deliverable ? Event.DeliveryStatus.Pending : Event.DeliveryStatus.DeadLettered,
            LastError = deliverable ? null : "DESTINATION_UNAVAILABLE_AT_TRANSITION" };
        var fence = new List<SurrealQuery>();
        // raw: the exact observed destination or absence is part of the surrounding atomic lifecycle transition.
        if (tenantRow is not null)
        {
            fence.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<User>(receipt.TenantId.ToString("N"))
                .Where(row => row.IsActive == tenantRow.IsActive).Set(row => row.IsActive, tenantRow.IsActive).Build(),
                scope + "_destination_tenant", "_consent_destination_tenant"));
            fence.Add(SurrealQuery.Of("IF $_consent_destination_tenant = NONE OR $_consent_destination_tenant = [] { THROW 'CONSENT_LIFECYCLE_CHANGED' }"));
        }
        if (registration is not null)
        {
            fence.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Registration>(notification.RegistrationId!)
                .Where(row => row.Version == registration.Version && row.TenantId == tenant
                    && row.IsActive == registration.IsActive && row.DestinationRevision == registration.DestinationRevision
                    && row.Url == registration.Url && row.SecretRevision == registration.SecretRevision)
                .Set(row => row.Version, registration.Version).Build(), scope + "_registration", "_consent_registration"));
            fence.Add(SurrealQuery.Of("IF $_consent_registration = NONE OR $_consent_registration = [] { THROW 'CONSENT_LIFECYCLE_CHANGED' }"));
        }
        else
        {
            fence.Add(SurrealGeneratedStatementScope.Assign(SurrealQuery<Registration>.From()
                .Where(row => row.TenantId == tenant).Limit(1), scope + "_registration_missing", "_consent_registration_missing"));
            fence.Add(SurrealQuery.Of("IF array::len($_consent_registration_missing) != 0 { THROW 'CONSENT_LIFECYCLE_CHANGED' }"));
        }
        return new ConsentNotificationPlan(notification, new Audit(), fence).ForTransition(receipt, type, occurredAt, detail);
    }
}
